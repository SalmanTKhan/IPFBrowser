using System;
using System.IO;
using IPFBrowser.FileFormats.IPF;

namespace IpfBrowserCLI
{
	internal class Program
	{
		static void Main(string[] args)
		{
			if (args.Length < 1)
			{
				Console.WriteLine("Usage: ipfbrowsercli <input folder> [-o <output.ipf>] [-nv <new version>] [-ov <old version>]");
				return;
			}

			string inputFolderPath = args[0];
			string outputIpfPath = null;
			string packFileName = null;
			uint newVersion = 1000000;
			uint oldVersion = 0;

			for (int i = 1; i < args.Length; i += 2)
			{
				switch (args[i])
				{
					case "-o":
						outputIpfPath = args[i + 1];
						break;
					case "-nv":
						if (!uint.TryParse(args[i + 1], out newVersion))
						{
							Console.WriteLine("Invalid new version value.");
							return;
						}
						break;
					case "-ov":
						if (!uint.TryParse(args[i + 1], out oldVersion))
						{
							Console.WriteLine("Invalid old version value.");
							return;
						}
						break;
					case "-p":
						packFileName = args[i + 1];
						break;
					default:
						Console.WriteLine($"Unknown argument: {args[i]}");
						return;
				}
			}

			outputIpfPath ??= $"{newVersion}_001001.ipf";

			try
			{
				var ipf = new Ipf(oldVersion, newVersion);
				if (packFileName == null)
					ipf.AddFolder(inputFolderPath);
				else
					ipf.AddFolder(packFileName, inputFolderPath);
				ipf.Save(outputIpfPath);

				Console.WriteLine($"Created {outputIpfPath} from {inputFolderPath}");
			}
			catch (Exception ex)
			{
				Console.WriteLine($"Error creating {outputIpfPath}: {ex.Message}");
			}
		}
	}
}