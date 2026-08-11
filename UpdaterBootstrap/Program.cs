using System.Diagnostics;



internal static class Program

{

    private const string UpdaterStagingDirectoryName = ".updater-staging";



    private static int Main(string[] args)

    {

        Console.WriteLine("Updater Bootstrapper");



        bool noRelaunch = false;

        List<string> positionalArgs = new();



        foreach (string arg in args)

        {

            if (string.Equals(arg, "--no-relaunch", StringComparison.OrdinalIgnoreCase))

            {

                noRelaunch = true;

            }

            else

            {

                positionalArgs.Add(arg);

            }

        }



        if (positionalArgs.Count < 2)

        {

            Console.Error.WriteLine("Usage: UpdaterBootstrap <old-exe> <new-exe> [pid] [timeout-ms] [--no-relaunch]");

            return 1;

        }



        string oldExe = Path.GetFullPath(positionalArgs[0]);

        string newExe = Path.GetFullPath(positionalArgs[1]);

        int? pid = null;



        if (positionalArgs.Count >= 3

            && !string.IsNullOrWhiteSpace(positionalArgs[2])

            && int.TryParse(positionalArgs[2], out int parsedPid))

        {

            pid = parsedPid;

        }



        int timeoutMs = 30000;

        if (positionalArgs.Count >= 4 && int.TryParse(positionalArgs[3], out int parsedTimeout))

        {

            timeoutMs = parsedTimeout;

        }



        if (!File.Exists(newExe))

        {

            Console.Error.WriteLine($"Staged Updater binary not found: {newExe}");

            return 1;

        }



        if (!File.Exists(oldExe))

        {

            Console.Error.WriteLine($"Current Updater binary not found: {oldExe}");

            return 1;

        }



        string backupPath = oldExe + ".bak";

        bool movedToBackup = false;



        try

        {

            if (pid.HasValue)

            {

                try

                {

                    using Process process = Process.GetProcessById(pid.Value);

                    Console.WriteLine($"Waiting for current updater process {pid.Value} to exit before replacing the binary.");

                    if (!process.WaitForExit(timeoutMs))

                    {

                        Console.Error.WriteLine(

                            $"Current updater process {pid.Value} did not exit within {timeoutMs} ms. Aborting file swap.");

                        return 1;

                    }

                }

                catch (ArgumentException)

                {

                    Console.WriteLine($"Current updater process {pid.Value} has already exited; continuing with replacement.");

                }

            }

            else

            {

                Console.WriteLine("No updater process id was provided. Waiting a short safety window before replacement.");

                Thread.Sleep(1000);

            }



            if (File.Exists(backupPath))

            {

                File.Delete(backupPath);

            }



            if (File.Exists(oldExe))

            {

                File.Move(oldExe, backupPath);

                movedToBackup = true;

            }



            File.Copy(newExe, oldExe, overwrite: true);

            movedToBackup = false;



            Console.WriteLine($"Bootstrap replaced {oldExe}");



            if (IsUnderUpdaterStaging(newExe))

            {

                try

                {

                    File.Delete(newExe);

                    Console.WriteLine($"Removed staged updater binary: {newExe}");

                }

                catch (Exception cleanupEx)

                {

                    Console.WriteLine($"Warning: could not remove staged updater binary: {cleanupEx.Message}");

                }

            }



            if (!noRelaunch)

            {

                ProcessStartInfo startInfo = new()

                {

                    FileName = oldExe,

                    UseShellExecute = true,

                    WorkingDirectory = Path.GetDirectoryName(oldExe)

                };



                Process.Start(startInfo);

            }

            else

            {

                Console.WriteLine("Skipping updater relaunch (--no-relaunch).");

            }



            return 0;

        }

        catch (Exception ex)

        {

            if (movedToBackup && File.Exists(backupPath))

            {

                try

                {

                    if (File.Exists(oldExe))

                    {

                        File.Delete(oldExe);

                    }



                    File.Move(backupPath, oldExe);

                    Console.WriteLine($"Restored previous updater binary from backup: {oldExe}");

                }

                catch (Exception restoreEx)

                {

                    Console.Error.WriteLine($"Failed to restore updater binary from backup: {restoreEx.Message}");

                }

            }



            Console.Error.WriteLine($"Bootstrap failed: {ex.Message}");

            return 1;

        }

    }



    private static bool IsUnderUpdaterStaging(string path)

    {

        string? parentDirectory = Path.GetDirectoryName(path);

        return parentDirectory is not null

            && string.Equals(Path.GetFileName(parentDirectory), UpdaterStagingDirectoryName, StringComparison.OrdinalIgnoreCase);

    }

}


