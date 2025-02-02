﻿using System;
using System.Reflection;
using System.Collections.Generic;
using System.Linq;
using System.IO;

namespace G_CLI
{
    class Program
    {
        static bool connected = false;
        static bool stop = false;
        static int exitCode = 0;

        static int Main(string[] args)
        {
            
            Output output = Output.Instance;

            string[] cliArgs, lvArgs;
            lvComms lvInterface = new lvComms();
            lvMsg latestMessage = new lvMsg("NOOP", "");
            LvLauncher launcher = null;
            CliOptions options = new CliOptions();
            lvVersion current = LvVersions.CurrentVersion;
            portRegistration portRegistration = new portRegistration();

            splitArguments(args, out cliArgs, out lvArgs);
            if(!CommandLine.Parser.Default.ParseArguments(cliArgs, options))
            {
                Environment.Exit(CommandLine.Parser.DefaultExitCodeFail);
            }
            if (options.Version)
            {
                output.writeMessage(getVersionString());
                Environment.Exit(0);
            }

            output.setVerbose(options.Verbose);
            output.writeInfo("G CLI Started - Verbose Mode");
            output.writeInfo("Version " + getVersionString());
            output.writeInfo("G CLI Arguments: " + String.Join(" ", cliArgs));
            output.writeInfo("Arguments passed to LabVIEW: " + String.Join(" ", lvArgs));

            //Warn about deprecated option.
            if (options.lvExe != null)
            {
                output.writeError("--lv-exe option is deprecated. Alter the script to use --lv-ver. Default will be used this time.");
            }

            //Force a rescan at this point to get an output - the constructor scan does not seem to write to output, possibly due to happening before we set verbose flag.
            LvVersions.Scan();
            
            if (options.noLaunch)
            {
                output.writeMessage("Auto Launch Disabled");
                // disable timeout if noLaunch is specified
                options.timeout = -1;
            }
            else
            {
                // check launch vi
                if(options.LaunchVI == null)
                {
                    output.writeError("No launch VI supplied!");
                    return 1;
                }
                /*
                if (!File.Exists(options.LaunchVI))
                {
                    output.writeError("File \"" + options.LaunchVI + "\" does not exist!");
                    return 1;
                }
                */

                //Add VI extension if none specified.
                if(!Path.HasExtension(options.LaunchVI))
                {
                    output.writeInfo("No extension so assuming .vi");
                    options.LaunchVI = options.LaunchVI + ".vi";
                }

                List<string> permittedExtensions = new List<string>{ ".vi", ".lvproj", ".exe" };
                string ext = Path.GetExtension(options.LaunchVI).ToLower();
                if (!permittedExtensions.Contains(ext))
                {
                    output.writeError("Cannot handle *" + ext + " files");
                    return 1;
                }

                try
                {
                    launcher = new LvLauncher(options.LaunchVI, lvPathFinder(options), lvInterface.port, portRegistration);
                    launcher.Exited += Launcher_Exited;
                    launcher.Start();
                }
                catch(KeyNotFoundException ex)
                {
                    // Fail gracefully if lv-ver option cannot be resolved
                    string bitness = options.x64 ? " 64bit" : string.Empty;
                    output.writeError("LabVIEW version \"" + options.lvVer + bitness + "\" not found!");
                    output.writeMessage("Available LabVIEW versions are:");
                    foreach(var ver in LvVersions.Versions)
                    {
                        output.writeMessage(ver.ToString());
                    }
                    return 1;
                }
                catch(FileNotFoundException ex)
                {
                    output.writeError("ERROR: Missing File: " + ex.Message);
                    return 1;
                }                    
            }

            //At this point LV should have launched so now we need to handle Ctrl+C to ensure LV is killed as well.
            Console.CancelKeyPress += delegate (object sender, ConsoleCancelEventArgs e)
            {
                output.writeMessage("Cancel key received, closing LabVIEW.");
                launcher.Kill();
            };

            // wait for the LabVIEW application to connect to the cli
            connected = lvInterface.waitOnConnection(options.timeout);
            portRegistration.unRegister();

            // if timed out, kill LabVIEW and exit with error code
            if (!connected && launcher!=null)
            {
                output.writeError("Connection to LabVIEW timed out!");
                launcher.Kill();
                launcher.Exited -= Launcher_Exited;
                return 1;
            }
            else
            {

                output.writeInfo("Client Connected");
            }

            //Write the use arguments
            lvInterface.writeInitialMessages(lvArgs, Directory.GetCurrentDirectory());

            while (!stop)
            {
                //strange call because it is async method.
                latestMessage = lvInterface.readMessage().GetAwaiter().GetResult();

                switch (latestMessage.messageType)
                {
                    case "OUTP":
                        Console.Write(latestMessage.messageData);
                        break;
                    case "EXIT":
                        exitCode = lvInterface.extractExitCode(latestMessage.messageData);
                        output.writeInfo("Received Exit Code " + exitCode);
                        stop = true;
                        break;
                    case "RDER":
                        exitCode = 1;
                        output.writeError("Read Error");
                        if(latestMessage.messageData != "")
                        {
                            output.writeError(": " + latestMessage.messageData);
                        }
                        output.writeError("Since the network stream will be out of sync the application will now exit.");
                        stop = true;
                        break;
                    default:
                        output.writeError("Unknown Message Type Received:" + latestMessage.messageType);
                        break;
                }

            };

            // close tcp listener
            lvInterface.Close();

            // if killswitch is set, force LabVIEW to exit if it has not closed by itself after a 10s timeout (or the period specified in --timeout)
            if (options.kill)
            {
                int timeout = options.timeout == -1 ? 10000 : options.timeout;
                if (!launcher.lvExited.Wait(timeout))
                {
                    output.writeMessage("Forcing LabVIEW to terminate...");
                    launcher.Kill();
                }
            }

            launcher.Close();

            return exitCode;
        }



        private static void Launcher_Exited(object sender, EventArgs e)
        {
            // Just quit by force if the tcp connection was not established or if LabVIEW exited without sending "EXIT" or "RDER"
            if (!connected || !stop)
            {
                Output output = Output.Instance;
                output.writeError("LabVIEW terminated unexpectedly!");
                exitCode = 1;
                stop = true; //in case the system is still waiting on the connection.
            }
        }

        private static void splitArguments(string[] args, out string[] cliArgs, out string[] lvArgs)
        {

            int splitterLocation = -1;

            for(int i = 0; i < args.Length; i++)
            {
                if(args[i] == "--")
                {
                    splitterLocation = i;
                }
            }

            if(splitterLocation > 0)
            {
                cliArgs = args.Take(splitterLocation).ToArray();
                lvArgs = args.Skip(splitterLocation + 1).ToArray();
            }
            else
            {
                cliArgs = args;
                lvArgs = new string[0];
            }

        }
        private static lvVersion lvPathFinder(CliOptions options)
        {
            if (options.lvVer != null)
            {
                try
                {
                    return LvVersions.ResolveVersionString(options.lvVer, options.x64);
                }
                catch(KeyNotFoundException ex)
                {
                    throw; // So the exception makes it to the handler above.
                }
            }
            if (LvVersions.CurrentVersion != null)
            {
                return LvVersions.CurrentVersion;
            }
            else
            {
                return new lvVersion();
            }
        }

        private static String getVersionString()
        {

            String bitness;

            if(Assembly.GetExecutingAssembly().GetName().ProcessorArchitecture.ToString() == "Amd64")
            {
                bitness = "64-Bit";
            }
            else
            {
                bitness = "32-Bit";
            }
            return Assembly.GetExecutingAssembly().GetName().Version + " " + bitness;
        }

    }


}
