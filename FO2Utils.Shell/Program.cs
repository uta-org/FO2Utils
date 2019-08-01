using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using FO2Utils.Shell.Properties;
using Newtonsoft.Json;
using Console = Colorful.Console;

namespace FO2Utils.Shell
{
    internal class Program
    {
        private static Color PromptColor => Color.DodgerBlue;
        private static Settings Settings => Settings.Default;
        private static List<string> FO2DataContents { get; set; } = new List<string>();

        private static void Main(string[] args)
        {
            const string errorMsg0 = "Can't access {0} path, please specify it on the xml settings...";

            if (string.IsNullOrEmpty(Settings.FO2Path) || !Directory.Exists(Settings.FO2Path))
            {
                ShowError(string.Format(errorMsg0, "Flat Out 2"));
                return;
            }

            if (string.IsNullOrEmpty(Settings.PatchFilePath) || !File.Exists(Settings.PatchFilePath))
            {
                ShowError(string.Format(errorMsg0, "patch"));
                return;
            }

            if (string.IsNullOrEmpty(Settings.BFS2PackPath) || !File.Exists(Settings.BFS2PackPath))
            {
                ShowError(string.Format(errorMsg0, "BFS2Pack"));
                return;
            }

            if (Path.GetExtension(Settings.BFS2PackPath).ToLowerInvariant() != ".exe")
            {
                ShowError("BFS2Pack isn't a executable...");
                return;
            }

            string dataFolder = Path.Combine(Settings.FO2Path, "data");
            string tempFolder = Path.Combine(Settings.FO2Path, "temp");

            if (!Directory.Exists(tempFolder))
                Directory.CreateDirectory(tempFolder);

            if (!Directory.Exists(dataFolder))
            {
                ShowError("Can't access data folder, please check that FO2 contains one...");
                return;
            }

            string[] lines = File.ReadAllLines(Settings.PatchFilePath);
            var bfsFiles = lines.Select(line => Path.Combine(Path.GetDirectoryName(Settings.PatchFilePath), line));
            var bfsFolders = bfsFiles.Select(line => GetActualTempFolder(tempFolder, line));
            var bfsDict = bfsFolders.Select(folder => new { Folder = folder, Files = GetFolderFiles(folder), ConflictingFiles = GetConflictingFiles(folder) })
                .ToDictionary(t => t.Folder + "\\", t => new Tuple<List<string>, List<string>>(t.Files, t.ConflictingFiles));

            string dictJson = Path.Combine(tempFolder, "dict.json");
            File.WriteAllText(dictJson, JsonConvert.SerializeObject(bfsDict, Formatting.Indented));

            foreach (var bfsFile in bfsFiles)
            {
                if (!File.Exists(bfsFile) || Path.GetExtension(bfsFile).ToLowerInvariant() != ".bfs")
                    continue;

                string actualTempPath = GetActualTempFolder(tempFolder, bfsFile);

                if (!Directory.Exists(actualTempPath))
                {
                    Directory.CreateDirectory(actualTempPath);
                    CreateProcess(Settings.BFS2PackPath, $"x {bfsFile} -v", actualTempPath);
                }

                var inFiles = bfsDict[actualTempPath].Item1;
                const string backupFilename = "backup-database.json";
                Dictionary<string, List<Tuple<int, string>>> currentDictionary;

                int index = 0;
                foreach (var inFile in inFiles)
                {
                    string realFile = Path.Combine(actualTempPath, inFile);
                    string replFile = Path.Combine(Settings.FO2Path, inFile);

                    bool exists = File.Exists(replFile);
                    if (exists)
                    {
                        string folder = Path.GetDirectoryName(replFile);
                        string database = Path.Combine(folder, backupFilename);

                        int ocurrences = Directory.GetFiles(folder, "*.backup", SearchOption.TopDirectoryOnly).Length;

                        File.Move(replFile, replFile + $".backup{ocurrences}");

                        string fileName = Path.GetFileName(replFile);

                        // Load current dictionary...
                        currentDictionary =
                            File.Exists(database) ? JsonConvert.DeserializeObject<Dictionary<string, List<Tuple<int, string>>>>(
                                File.ReadAllText(database)) : new Dictionary<string, List<Tuple<int, string>>>();

                        bool save = false;

                        if (!currentDictionary.ContainsKey(fileName))
                        {
                            var list = new List<Tuple<int, string>>(); // ocurrences = 0, in this scope
                            list.Add(new Tuple<int, string>(ocurrences, actualTempPath));

                            currentDictionary.Add(fileName, list);
                            save = true;
                        }
                        else
                        {
                            var list = currentDictionary[fileName];
                            if (list.Count < ocurrences)
                            {
                                list.Add(new Tuple<int, string>(ocurrences, actualTempPath));
                                save = true;
                            }
                        }

                        if (save)
                        {
                            File.WriteAllText(database, JsonConvert.SerializeObject(currentDictionary));
                        }
                    }

                    File.Copy(realFile, replFile);

                    float perc = (float)index / inFiles.Count;
                    Console.WriteLine($"[{perc:F2}%] Moving file {index}: {Path.GetFileName(realFile)}...{(exists ? " [REPLACING...]" : string.Empty)}");

                    ++index;
                }
            }

            Console.WriteLine($"{bfsDict.Values.Sum(v => v.Item2.Count)} total intersections...");
            Console.WriteLine("Completed!", Color.LimeGreen);
            AnyKeyToExit();
        }

        private static string GetActualTempFolder(string folder, string actualFile)
        {
            return Path.Combine(folder, Path.GetFileNameWithoutExtension(actualFile));
        }

        private static List<string> GetConflictingFiles(string folder)
        {
            if (FO2DataContents.Count == 0)
                FO2DataContents = GetFolderFiles(Settings.FO2Path);

            var folderContents = GetFolderFiles(folder);
            return FindCommon(new[] { FO2DataContents, folderContents }).ToList();
        }

        private static List<T> FindCommon<T>(IEnumerable<List<T>> lists)
        {
            Dictionary<T, int> map = new Dictionary<T, int>();
            int listCount = 0; // number of lists

            foreach (IEnumerable<T> list in lists)
            {
                listCount++;
                foreach (T item in list)
                {
                    // Item encountered, increment count
                    int currCount;
                    if (!map.TryGetValue(item, out currCount))
                        currCount = 0;

                    currCount++;
                    map[item] = currCount;
                }
            }

            List<T> result = new List<T>();
            foreach (KeyValuePair<T, int> kvp in map)
            {
                // Items whose occurrence count is equal to the number of lists are common to all the lists
                if (kvp.Value == listCount)
                    result.Add(kvp.Key);
            }

            return result;
        }

        private static List<string> GetFolderFiles(string folder)
        {
            const string dataLiteral = @"\data\";
            return Directory.GetFiles(folder, "*.*", SearchOption.AllDirectories)
                .Where(file => file.ToLowerInvariant().Contains(dataLiteral))
                .Select(file => file.Substring(file.ToLowerInvariant().IndexOf(dataLiteral) + 1))
                .ToList();
        }

        private static void GetExecutingString(ProcessStartInfo info)
        {
            Console.WriteLineFormatted("Executing: '{0}' at '{1}'", PromptColor, Color.White, $"{info.FileName} {info.Arguments}", info.WorkingDirectory);
        }

        private static void CreateProcess(string fileName, string arguments, string workingDir)
        {
            CreateProcess(fileName, arguments, workingDir, null);
        }

        private static void CreateProcess(string fileName, string arguments, string workingDir, DataReceivedEventHandler outputHandler)
        {
            CreateProcess(fileName, arguments, workingDir, null, outputHandler);
        }

        private static void CreateProcess(string fileName, string arguments, string workingDir,
            Func<bool> continueFunc, DataReceivedEventHandler outputHandler)
        {
            if (string.IsNullOrEmpty(fileName))
                throw new ArgumentNullException(nameof(fileName));

            if (string.IsNullOrEmpty(arguments))
                throw new ArgumentNullException(nameof(arguments));

            if (string.IsNullOrEmpty(workingDir))
                throw new ArgumentNullException(nameof(workingDir));

            using (var process =
                new Process
                {
                    StartInfo =
                        new ProcessStartInfo(fileName, arguments)
                        {
                            WorkingDirectory = workingDir,
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true
                        }
                })
            {
                if (continueFunc?.Invoke() == true)
                    return;

                GetExecutingString(process.StartInfo);

                process.OutputDataReceived += outputHandler ?? ((sender, e) => ProcessOnErrorDataReceived(e, false));
                process.ErrorDataReceived += (sender, e) => ProcessOnErrorDataReceived(e, outputHandler != null);
                process.Start();

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                process.WaitForExit();
            }
        }

        private static void ProcessOnErrorDataReceived(DataReceivedEventArgs e, bool displayRed)
        {
            if (string.IsNullOrEmpty(e.Data))
            {
                Console.WriteLine();
                return;
            }

            if (displayRed)
            {
                Console.WriteLine(e.Data, Color.Red);
            }
            else
            {
                System.Console.WriteLine(e.Data);
            }
        }

        private static void ShowError(string errorMsg)
        {
            Console.WriteLine(errorMsg, Color.Red);
            AnyKeyToExit();
        }

        private static void AnyKeyToExit()
        {
            Console.Write("Press any key to exit...");
            Console.ReadKey();
        }
    }
}