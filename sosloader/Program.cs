using Microsoft.Diagnostics.Runtime;
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace sosloader
{
    class DacLocator
    {
	    [DllImport("dbghelp.dll", SetLastError = true)]
        static extern bool SymInitialize(IntPtr hProcess, String symPath, bool fInvadeProcess);

        [DllImport("dbghelp.dll", SetLastError = true)]
        static extern bool SymCleanup(IntPtr hProcess);

        [DllImport("dbghelp.dll", SetLastError = true)]
        static extern bool SymFindFileInPath(IntPtr hProcess, String searchPath, String filename, uint id, uint two, uint three, uint flags, StringBuilder filePath, IntPtr callback, IntPtr context);

        private static void VerifyHr(int hr)
        {
            if (hr != 0)
                throw Marshal.GetExceptionForHR(hr);
        }

        /// <summary>
        /// Retrieves the debug support files (DAC, SOS, CLR) from the Microsoft symbol server
        /// and returns the path to the temporary directory in which they were placed. If the <paramref name="storageLocation"/>
        /// parameter is not null/empty, the files will be stored in that location.
        /// </summary>
        /// <param name="clrInfo">The CLR version for which to load the support files.</param>
        public static string GetDebugSupportFiles(ClrInfo clrInfo, DataTarget target, string storageLocation = null)
        {
            IntPtr processHandle = Process.GetCurrentProcess().Handle;
            if (!SymInitialize(processHandle, null, false))
            {
                Console.WriteLine("*** Error initializing dbghelp.dll symbol support");
                return null;
            }
            if (string.IsNullOrEmpty(storageLocation))
            {
                storageLocation = Path.Combine(Path.GetTempPath(), clrInfo.Version.ToString());
                if (!Directory.Exists(storageLocation))
                    Directory.CreateDirectory(storageLocation);
            }

            Console.WriteLine("CLR version: " + clrInfo.Version);
            var clrModuleName = (clrInfo.Version.Major == 2 ? "mscorwks" : "clr")+ ".dll";
	        var clrVersion = clrInfo.Version;

	        LoadSymbol(clrInfo, storageLocation, processHandle, clrModuleName, clrModuleName);
			LoadSymbol(clrInfo, storageLocation, processHandle, $"sos_{(IntPtr.Size == 4 ? "x86" : "amd64")}_{target.Architecture}_{clrVersion.Major}.{clrVersion.Minor}.{clrVersion.Revision}.{clrVersion.Patch:D2}.dll", "SOS.dll");
			LoadSymbol(clrInfo, storageLocation, processHandle, clrInfo.DacInfo.FileName, "mscordacwks.dll");

            SymCleanup(processHandle);
            return storageLocation;
        }

	    private static void LoadSymbol(ClrInfo clrInfo, string storageLocation, IntPtr processHandle, string filename,
		    string filename2)
	    {

		    for (int i = 0; i < 100; i++)
		    {
			    var loadedClrFile = new StringBuilder(2048);
			    if (!SymFindFileInPath(processHandle, null, filename, clrInfo.DacInfo.TimeStamp,
				    clrInfo.DacInfo.FileSize, 0, 0x2, loadedClrFile, IntPtr.Zero, IntPtr.Zero))
			    {
				    Console.WriteLine(
					    $"*** Error retrieving {filename2} from symbol server. ErrorCode={Marshal.GetLastWin32Error()}");
				    Thread.Sleep(10);
			    }
			    else
			    {
				    var destFileName = Path.Combine(storageLocation, filename2);
					Console.WriteLine(destFileName);
				    File.Copy(loadedClrFile.ToString(), destFileName, true);
				    break;
			    }
		    }
	    }
    }

    class Program
    {
        static void Main(string[] args)
        {
	        try
	        {

				if (args.Length == 1 && args[0].EndsWith(".dmp", StringComparison.InvariantCultureIgnoreCase))
				{
					args = new[] { "launch", args[0] };
				}

				if (args.Length != 2 || (args[0] != "download" && args[0] != "launch"))
				{
					Console.WriteLine("Usage: sosloader <download | launch> <dump file path>");
					return;
				}

				Console.WriteLine("\nPlease make sure that you have dbghelp.dll and symsrv.dll accessible\nto the application. Otherwise, we will not be able to retrieve files\nfrom the Microsoft symbol server.\n");

				string dumpFilePath = args[1];
				DataTarget target = DataTarget.LoadCrashDump(dumpFilePath);
				if (target.ClrVersions.Count == 0)
				{
					Console.WriteLine("This dump file does not have a CLR loaded in it.");
					return;
				}
				if (target.ClrVersions.Count > 1)
				{
					Console.WriteLine("This dump file has multiple CLR versions loaded in it.");
					return;
				}

				if (target.Architecture == Architecture.X86 && IntPtr.Size != 4)
				{
					Console.WriteLine("Please use the 32 bit version of sosloader to analyze this dump.");
					return;
				}
				if (target.Architecture == Architecture.Amd64 && IntPtr.Size != 8)
				{
					Console.WriteLine("Please use the 64 bit version of sosloader to analyze this dump.");
					return;
				}

				string dacLocation = target.ClrVersions[0].LocalMatchingDac;
				if (!String.IsNullOrEmpty(dacLocation))
				{
					Console.WriteLine();
					Console.WriteLine($"dacLocation: {dacLocation}");
					Console.WriteLine("The debug support files are available on the local machine.");
					if (args[0] == "launch")
					{
						Console.WriteLine("Launching windbg.exe with the provided dump file...");
						var loadByCommand = ".loadby sos " + (target.ClrVersions[0].Version.Major == 4 ? "clr" : "mscorwks");

						StartWinDbg($"-z {dumpFilePath} -c \"{loadByCommand}\"");
					}
					return;
				}

				string debugSupportFilesLocation = DacLocator.GetDebugSupportFiles(target.ClrVersions[0], target);
				if (args[0] == "launch")
				{
					Console.WriteLine("Launching windbg.exe with the provided dump file (must be in path)...");
					var loadCommand = String.Format(".load {0}; .cordll -se -lp {1}",
						Path.Combine(debugSupportFilesLocation, "sos"), debugSupportFilesLocation);

					StartWinDbg($"-z {dumpFilePath} -c \"{loadCommand}\"");
				}
				else
				{
					Console.WriteLine("Debug support files are now available in " + debugSupportFilesLocation);
					Console.WriteLine("Use .load <location>\\sos to load SOS and .cordll -se -lp <location> to set up DAC");
				}
			}
	        finally
	        {
				Console.WriteLine("press any key to pause");
		        Thread.Sleep(5000);
		        if (Console.KeyAvailable)
		        {
			        Console.WriteLine("|| paused ||");
					Thread.Sleep(1000000);
		        }

	        }
        }

	    private static void StartWinDbg(string arguments)
	    {
		    var winDbg = @"C:\Program Files\Debugging Tools for Windows (x64)\windbg.exe";
		    Console.WriteLine();
		    Console.WriteLine($"start>: {winDbg} {arguments}");
		    Process.Start(winDbg, arguments);
	    }
    }
}
