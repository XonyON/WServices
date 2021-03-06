﻿using Microsoft.ServiceBus;
using Microsoft.ServiceBus.Messaging;
using MigraDoc.DocumentObjectModel;
using MigraDoc.Rendering;
using PdfSharp.Pdf;
using SmartScanService;
using System;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Formatters.Binary;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace FileProcessingService
{
	public class FileService
	{
        private const string imageQueue = "pdfqueue";
        private const string settingQueue = "settingsqueue";
        
        private int timeOutBeetwenProccess = 5000;
        private string[] availableFormatsToProccess = new[] { @"^image_[0-9].(jpg|jpeg)$", @"^screen_[0-9].(png)$" };

        private string inDirectory;
		private string outDirectory;
		private string tempDirectory;
        private string queueDirectory;

        private FileSystemWatcher watcher;
		private Task processTask;
		private CancellationTokenSource tokenSource;
		private AutoResetEvent autoResetEvent;
		private Document document;
		private Section section;
		private PdfDocumentRenderer pdfRenderer;
        private NamespaceManager nsManager;

        private int TimeoutWatcher
        {
            get
            {
                return timeOutBeetwenProccess;
            }
            set
            {
                this.timeOutBeetwenProccess = value;
            }
        }

        private string BreakBarCode { get; set; }

        public FileService(string inDir, string outDir, string tempDir, string queueDir)
		{
			inDirectory = inDir;
			outDirectory = outDir;
			tempDirectory = tempDir;
            queueDirectory = queueDir;

            if (!Directory.Exists(inDirectory))
			{
				Directory.CreateDirectory(inDirectory);
			}

			if (!Directory.Exists(outDirectory))
			{
				Directory.CreateDirectory(outDirectory);
			}

			if (!Directory.Exists(tempDirectory))
			{
				Directory.CreateDirectory(tempDirectory);
			}
            if (!Directory.Exists(queueDir))
            {
                Directory.CreateDirectory(queueDir);
            }

            watcher = new FileSystemWatcher(inDirectory);
			watcher.Created += Watcher_Created;
			tokenSource = new CancellationTokenSource();
			processTask = new Task(() => WorkProcedure(tokenSource.Token));
			autoResetEvent = new AutoResetEvent(false);
            nsManager = NamespaceManager.Create();
        }

		public void WorkProcedure(CancellationToken token)
		{
            Console.Clear();

            this.CheckAndApplySettings();

			var currentImageIndex = -1;
			var imageCount = 0;
			var watingForTheNextPage = false;

			CreateNewDocument();

			do
			{
				foreach (var file in Directory.EnumerateFiles(inDirectory).Skip(imageCount))
				{
					var fileName = Path.GetFileName(file);
                    var isValidFormat = IsValidFormat(fileName);
                    Console.WriteLine($"The image {fileName} format validation result is '{isValidFormat}'");

                    if (isValidFormat)
					{
						var imageIndex = GetIndex(fileName);

						if (watingForTheNextPage && imageIndex != currentImageIndex + 1 && currentImageIndex != -1)
						{
							SaveDocument();
							CreateNewDocument();
							watingForTheNextPage = false;
						}

						if (OpenFile(file, 3))
						{
							AddImageToDocument(file);
							imageCount++;
							currentImageIndex = imageIndex;
							watingForTheNextPage = true;
						}
					}
					else
					{
						var outFile = Path.Combine(tempDirectory, fileName);
						if (OpenFile(file, 3))
						{
							if (File.Exists(outFile))
							{
								File.Delete(file);
							}
							else
							{
								File.Move(file, outFile);
							}
						}
					}
				}
				
				if (!autoResetEvent.WaitOne(timeOutBeetwenProccess) && watingForTheNextPage)
				{
					SaveDocument();
					CreateNewDocument();
					watingForTheNextPage = false;
				}

                var queue = QueueClient.Create(imageQueue);
                var message = queue.Receive(TimeSpan.FromSeconds(3));
                if (message != null)
                {
                    var pdfSerializable = message.GetBody<byte[]>();
                    if (pdfSerializable != null)
                    {
                        byte[] bytes = pdfSerializable;
                        File.WriteAllBytes(Path.Combine(outDirectory, $"queued_{DateTime.Now.Millisecond}.pdf"), bytes);
                    }

                    message.Complete();
                }

                queue.Close();

                if (token.IsCancellationRequested)
				{
					if(watingForTheNextPage)
					{
						SaveDocument();
					}

					foreach (var file in Directory.EnumerateFiles(inDirectory))
					{
						if (OpenFile(file, 3))
						{
							File.Delete(file);
						}
					}
				}
			}
			while (!token.IsCancellationRequested);
		}

		public void Start()
		{
			processTask.Start();

            if (!nsManager.QueueExists(imageQueue))
            {
                nsManager.CreateQueue(imageQueue);
            }

            this.ShareServiceSettings();

            watcher.EnableRaisingEvents = true;
		}

		public void Stop()
		{
            if (!nsManager.QueueExists(imageQueue))
            {
                nsManager.DeleteQueue(imageQueue);
            }

            watcher.EnableRaisingEvents = false;
			tokenSource.Cancel();
			processTask.Wait();
		}

        private void CreateNewDocument()
        {
            document = new Document();
            section = document.AddSection();
            pdfRenderer = new PdfDocumentRenderer();
        }

        private void ShareServiceSettings()
        {
            var queue = QueueClient.Create(settingQueue);

            var serializer = new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof(ServiceSettings));
            var settings = new ServiceSettings() { Timeout = this.TimeoutWatcher + 500, BreakBarCode = string.IsNullOrEmpty(this.BreakBarCode) ? "newCode" : this.BreakBarCode + "_!" };
            var stream = new MemoryStream();
            serializer.WriteObject(stream, settings);

            queue.Send(new BrokeredMessage(stream.ToArray()));
            queue.Close();
        }

        private void CheckAndApplySettings()
        {
            var queue = QueueClient.Create(settingQueue);
            var message = queue.Receive(TimeSpan.FromSeconds(2));
            if (message != null)
            {
                var serializedSettings = message.GetBody<byte[]>();
                if (serializedSettings != null)
                {
                    byte[] bytes = serializedSettings;
                    var serializer = new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof(ServiceSettings));
                    var stream = new MemoryStream(bytes);

                    try
                    {
                        var settings = (ServiceSettings)serializer.ReadObject(stream);
                        this.BreakBarCode = settings.BreakBarCode;
                        this.TimeoutWatcher = settings.Timeout;

                        Console.WriteLine($"New Settings was setup ({nameof(this.BreakBarCode)}: {this.BreakBarCode},{nameof(this.TimeoutWatcher)}: { this.TimeoutWatcher}");
                    }
                    catch
                    {
                        Console.WriteLine("Invalid settings");
                    }
                    finally
                    {
                        message.Complete();
                    }
                }

                message.Complete();
            }

            queue.Close();
        }

        private void SaveDocument()
        {
            var newDocumentIndex = Directory.GetFiles(outDirectory).Length + 1;
            var resultFile = Path.Combine(outDirectory, $"result_{newDocumentIndex}.pdf");

            pdfRenderer.Document = document;
            pdfRenderer.RenderDocument();

            var queue = QueueClient.Create(imageQueue);
            MemoryStream memorystream = new MemoryStream();
            pdfRenderer.Save(memorystream, true);

            queue.Send(new BrokeredMessage(memorystream.ToArray()));
            queue.Close();
        }

        private void AddImageToDocument(string file)
        {
            var image = section.AddImage(file);

            image.Height = document.DefaultPageSetup.PageHeight;
            image.Width = document.DefaultPageSetup.PageWidth;
            image.ScaleHeight = 0.75;
            image.ScaleWidth = 0.75;

            section.AddPageBreak();
        }

        private bool IsValidFormat(string fileName)
        {
            var isValid = false;

            foreach (var format in availableFormatsToProccess)
            {
                if (Regex.IsMatch(fileName, format))
                {
                    return true;
                }
            }


            return isValid;
        }

        private int GetIndex(string fileName)
        {
            var match = Regex.Match(fileName, @"[0-9]");

            return match.Success ? int.Parse(match.Value) : -1;
        }

        private void Watcher_Created(object sender, FileSystemEventArgs e)
        {
            autoResetEvent.Set();
        }

        private bool OpenFile(string fileName, int attempCount)
		{
			for (int i = 0; i < attempCount; i++)
			{
				try
				{
					var file = File.Open(fileName, FileMode.Open, FileAccess.Read, FileShare.None);
					file.Close();

					return true;
				}
				catch (IOException)
				{
					Thread.Sleep(timeOutBeetwenProccess);
				}
			}

			return false;
		}
	}
}
