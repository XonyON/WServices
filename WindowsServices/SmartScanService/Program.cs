using NLog;
using NLog.Config;
using NLog.Targets;
using System;
using System.IO;
using Topshelf;

namespace FileProcessingService
{
	class Program
	{
		static void Main(string[] args)
		{
			var baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
			var inFolder = Path.Combine(baseDirectory, "in");
			var outFolder = Path.Combine(baseDirectory, "out");
			var tempFolder = Path.Combine(baseDirectory, "temp");
            var queueFolder = Path.Combine(baseDirectory, "queue");

            var loggingConfiguration = new LoggingConfiguration();

			var fileTarget = new FileTarget()
			{
				Name = "Default",
				FileName = Path.Combine(baseDirectory, "log.txt"),
				Layout = "${date} ${message} ${onexception:inner=${exception:format=toString}}"
			};

			loggingConfiguration.AddTarget(fileTarget);
			loggingConfiguration.AddRuleForAllLevels(fileTarget);

			HostFactory.Run(
				configurator =>
				{
                    configurator.Service<FileService>(
						s =>
						{
							s.ConstructUsing(() => new FileService(inFolder, outFolder, tempFolder, queueFolder));
							s.WhenStarted(serv => serv.Start());
							s.WhenStopped(serv => serv.Stop());
						}).UseNLog(new LogFactory(loggingConfiguration));
                    configurator.SetServiceName("SmartScanService");
                    configurator.SetDisplayName("SmartScan Service");
                    configurator.StartAutomaticallyDelayed();
                    configurator.RunAsLocalService();
				});
		}
	}
}
