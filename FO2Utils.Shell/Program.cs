using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
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
                ShowError(string.Format(errorMsg0, "FlatOut 2"));
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
            var _allFiles = bfsFolders.Select(file => GetFolderFiles(file, false).AsEnumerable()).Select(x => x).Distinct().ToList();

            HashSet<string> allOrigFiles;
            string origJson = Path.Combine(tempFolder, "origFiles.json");

            if (!File.Exists(origJson))
            {
                allOrigFiles = new HashSet<string>(GetFolderFiles(dataFolder, false));
                File.WriteAllText(origJson, JsonConvert.SerializeObject(allOrigFiles, Formatting.Indented));
            }
            else
                allOrigFiles = JsonConvert.DeserializeObject<HashSet<string>>(File.ReadAllText(origJson));

            string dictJson = Path.Combine(tempFolder, "dict.json");
            File.WriteAllText(dictJson, JsonConvert.SerializeObject(bfsDict, Formatting.Indented));

            string allFilesJson = Path.Combine(tempFolder, "files.json");
            File.WriteAllText(allFilesJson, JsonConvert.SerializeObject(_allFiles, Formatting.Indented));

            // TODO: Make restore
            Console.Write("Do you wish to make a restore? [y/N]: ");
            if (Console.ReadLine().ToLowerInvariant() == "y")
            {
                const string backupString = ".backup0";

                var allFiles = Directory.GetFiles(dataFolder, "*.*", SearchOption.AllDirectories);
                var _allDataFiles = bfsFolders.Select(file => dataFolder + "\\" + GetFolderFiles(file).AsEnumerable()).Select(x => x).Distinct().ToList();

                var backupFiles = allFiles
                    .Where(file => Path.GetExtension(file)?.ToLowerInvariant() == backupString);

                var jsonFiles = allFiles
                    .Where(file => Path.GetExtension(file)?.ToLowerInvariant() == ".json");

                int index = 0;
                int totalCount = backupFiles.Count() + jsonFiles.Count();

                foreach (var backupFile in backupFiles)
                {
                    if (!File.Exists(backupFile))
                    {
                        Console.WriteLine($"[{GetPerc(index, totalCount)}] File doesn't exists! ({backupFile})", Color.Red);

                        ++index;
                        continue;
                    }

                    string origFile = backupFile.Replace(backupString, string.Empty);

                    if (File.Exists(origFile))
                        File.Delete(origFile);
                    else
                        Console.WriteLine($"[{GetPerc(index, totalCount)}] Couldn't delete modified file '{origFile}'...", Color.Yellow);

                    File.Move(backupFile, origFile);
                    ++index;
                }

                foreach (var jsonFile in jsonFiles)
                {
                    Console.WriteLine($"[{GetPerc(index, totalCount)}] Deleted json file: {jsonFile}...", PromptColor);

                    File.Delete(jsonFile);
                    ++index;
                }

                // Delete rest of files
                var aBackupFiles = allFiles
                    .Where(file => Path.GetExtension(file)?.ToLowerInvariant().Contains(".backup") == true);

                int aIndex = 0;
                int aTotalCount = aBackupFiles.Count();

                foreach (var aBackupFile in aBackupFiles)
                {
                    Console.WriteLine($"[{GetPerc(aIndex, aTotalCount)}] Deleted backup file: {aBackupFile}...", PromptColor);

                    File.Delete(aBackupFile);
                    ++index;
                }

                foreach (var moddedFile in _allDataFiles)
                {
                    if (allOrigFiles.Contains(moddedFile))
                        continue;

                    Console.WriteLine($"Deleted modded file: {moddedFile}...", PromptColor);
                    File.Delete(moddedFile);
                }
            }

            WriteSeparator();
            {
                int index = 0;
                int totalCount = bfsFiles.Count();
                float delta = 1f / totalCount;

                foreach (var bfsFile in bfsFiles)
                {
                    if (!File.Exists(bfsFile) || Path.GetExtension(bfsFile).ToLowerInvariant() != ".bfs")
                    {
                        ++index;
                        continue;
                    }

                    string actualTempPath = GetActualTempFolder(tempFolder, bfsFile) + "\\";

                    if (!Directory.Exists(actualTempPath))
                    {
                        Directory.CreateDirectory(actualTempPath);
                        CreateProcess(Settings.BFS2PackPath, $"x {bfsFile} -v", actualTempPath);
                    }

                    var inFiles = bfsDict[actualTempPath].Item1;

                    const string backupFilename = "backup-database.json";
                    const string backupSufix = "backup";

                    Dictionary<string, List<Tuple<int, string>>> currentDictionary;

                    int subindex = 0;

                    foreach (var inFile in inFiles)
                    {
                        string realFile = Path.Combine(actualTempPath, inFile);
                        string replFile = Path.Combine(Settings.FO2Path, inFile);

                        bool diffMD5 = CalculateMD5(realFile) != CalculateMD5(replFile);

                        if (!diffMD5)
                        {
                            Console.WriteLine($"Skipping same file... ({realFile} => {replFile})", Color.Yellow);

                            ++subindex;
                            continue;
                        }

                        bool exists = File.Exists(replFile) && diffMD5;
                        if (exists)
                        {
                            string folder = Path.GetDirectoryName(replFile);
                            string database = Path.Combine(folder, backupFilename);

                            // Get number of ocurrences
                            int ocurrences = Directory
                                .GetFiles(folder, $"{Path.GetFileNameWithoutExtension(replFile)}.*", SearchOption.TopDirectoryOnly)
                                .Count(file => Path.GetExtension(file).ToLowerInvariant().Contains(".backup"));

                            // Based on the number of backups then create *.backup(XX) files... *.backup0 will be always the original file
                            File.Move(replFile, replFile + $".{backupSufix}{ocurrences}");

                            string fileName = Path.GetFileName(replFile);

                            // Load current dictionary...
                            currentDictionary =
                                File.Exists(database) ? JsonConvert.DeserializeObject<Dictionary<string, List<Tuple<int, string>>>>(
                                    File.ReadAllText(database)) : new Dictionary<string, List<Tuple<int, string>>>();

                            bool save = false;

                            // Map the ocurrence with the folder where it comes from...
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

                            // If any changes made...
                            if (save)
                                File.WriteAllText(database,
                                    JsonConvert.SerializeObject(currentDictionary, Formatting.Indented));
                        }

                        if (!File.Exists(realFile))
                        {
                            Console.WriteLine($"Can't find file '{realFile}'...", Color.Yellow);
                            continue;
                        }

                        string replFolder = Path.GetDirectoryName(replFile);

                        if (!Directory.Exists(replFolder))
                            Directory.CreateDirectory(replFolder);

                        // TODO: Detect car file conflict (check if the conflicting folder contains a ini file...)
                        File.Copy(realFile, replFile);

                        string subPerc = GetPerc(subindex, inFiles.Count, out float subPercValue);
                        string perc = $"[{(float)index / totalCount + delta * subPercValue:F2} %]"; // GetPerc(index, totalCount);

                        Console.WriteLine($"[{subPerc} (Total: {perc})] Moving file {subindex}: {Path.GetFileName(realFile)}...{(exists ? " [REPLACING...]" : string.Empty)}");

                        ++subindex;
                    }

                    ++index;
                    WriteSeparator();
                }
            }

            Console.WriteLine($"{bfsDict.Values.Sum(v => v.Item2.Count)} total intersections...");
            Console.WriteLine("Completed!", Color.LimeGreen);
            AnyKeyToExit();
        }

        private static string CalculateMD5(string filename)
        {
            using (var md5 = MD5.Create())
            {
                using (var stream = File.OpenRead(filename))
                {
                    var hash = md5.ComputeHash(stream);
                    return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
                }
            }
        }

        private static void WriteSeparator(int count = 30)
        {
            Console.WriteLine(new string('=', count));
        }

        private static string GetPerc(int index, int total)
        {
            return GetPerc(index, total, out var perc);
        }

        private static string GetPerc(int index, int total, out float perc)
        {
            perc = (float)index / total * 100;
            return $"{perc:F2} %";
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

        private static List<string> GetFolderFiles(string folder, bool cutFile = true)
        {
            const string dataLiteral = @"\data\";
            return Directory.GetFiles(folder, "*.*", SearchOption.AllDirectories)
                .Where(file => file.ToLowerInvariant().Contains(dataLiteral))
                .Select(file => cutFile ? file.Substring(file.ToLowerInvariant().IndexOf(dataLiteral, StringComparison.Ordinal) + 1) : file)
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