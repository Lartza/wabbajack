using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Alphaleonis.Win32.Filesystem;
using Wabbajack.Common;
using Wabbajack.Lib.Downloaders;
using Wabbajack.VirtualFileSystem;
using Wabbajack.VirtualFileSystem.SevenZipExtractor;
using Directory = Alphaleonis.Win32.Filesystem.Directory;
using File = Alphaleonis.Win32.Filesystem.File;
using FileInfo = Alphaleonis.Win32.Filesystem.FileInfo;
using Path = Alphaleonis.Win32.Filesystem.Path;

namespace Wabbajack.Lib
{
    public abstract class AInstaller : ABatchProcessor
    {
        public bool IgnoreMissingFiles { get; internal set; } = false;

        public AbsolutePath OutputFolder { get; private set; }
        public AbsolutePath DownloadFolder { get; private set; }

        public abstract ModManager ModManager { get; }

        public AbsolutePath ModListArchive { get; private set; }
        public ModList ModList { get; private set; }
        public Dictionary<Hash, AbsolutePath> HashedArchives { get; } = new Dictionary<Hash, AbsolutePath>();
        
        public GameMetaData Game { get; }

        public SystemParameters? SystemParameters { get; set; }
        
        public bool UseCompression { get; set; }

        public TempFolder? ExtractedModlistFolder { get; set; } = null;


        public AInstaller(AbsolutePath archive, ModList modList, AbsolutePath outputFolder, AbsolutePath downloadFolder, SystemParameters? parameters, int steps, Game game)
            : base(steps)
        {
            ModList = modList;
            ModListArchive = archive;
            OutputFolder = outputFolder;
            DownloadFolder = downloadFolder;
            SystemParameters = parameters;
            Game = game.MetaData();
 
        }

        public async Task ExtractModlist()
        {
            ExtractedModlistFolder = await TempFolder.Create();
            await FileExtractor2.GatheringExtract(new NativeFileStreamFactory(ModListArchive), _ => true, 
                async (path, sfn) =>
                {
                    await using var s = await sfn.GetStream();
                    var fp = ExtractedModlistFolder.Dir.Combine(path);
                    fp.Parent.CreateDirectory();
                    await fp.WriteAllAsync(s);
                    return 0; 
                });
        }



        public void Info(string msg)
        {
            Utils.Log(msg);
        }

        public void Status(string msg)
        {
            Queue.Report(msg, Percent.Zero);
        }

        public void Error(string msg)
        {
            Utils.Log(msg);
            throw new Exception(msg);
        }

        public async Task<byte[]> LoadBytesFromPath(RelativePath path)
        {
            var fullPath = ExtractedModlistFolder!.Dir.Combine(path);
            if (!fullPath.IsFile) 
                throw new Exception($"Cannot load inlined data {path} file does not exist");
            
            return await fullPath.ReadAllBytesAsync();
        }

        public static ModList LoadFromFile(AbsolutePath path)
        {
            using var fs = new FileStream((string)path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var ar = new ZipArchive(fs, ZipArchiveMode.Read);
            var entry = ar.GetEntry("modlist");
            if (entry == null)
            {
                entry = ar.GetEntry("modlist.json");
                using (var e = entry.Open())
                    return e.FromJson<ModList>();
            }
            using (var e = entry.Open())
                return e.FromJson<ModList>();
        }

        /// <summary>
        ///     We don't want to make the installer index all the archives, that's just a waste of time, so instead
        ///     we'll pass just enough information to VFS to let it know about the files we have.
        /// </summary>
        protected async Task PrimeVFS()
        {
            VFS.AddKnown(ModList.Directives.OfType<FromArchive>().Select(d => d.ArchiveHashPath), HashedArchives);
            await VFS.BackfillMissing();
        }

        public void BuildFolderStructure()
        {
            Info("Building Folder Structure");
            ModList.Directives
                .Select(d => OutputFolder.Combine(d.To.Parent))
                .Distinct()
                .Do(f => f.CreateDirectory());
        }

        public async Task InstallArchives()
        {
            var grouped = ModList.Directives
                .OfType<FromArchive>()
                .Select(a => new {VF = VFS.Index.FileForArchiveHashPath(a.ArchiveHashPath), Directive = a})
                .GroupBy(a => a.VF)
                .ToDictionary(a => a.Key);

            if (grouped.Count == 0) return;
            
            await VFS.Extract(Queue, grouped.Keys.ToHashSet(), async (vf, sf) =>
            {
                await using var s = await sf.GetStream();
                foreach (var directive in grouped[vf])
                {
                    var file = directive.Directive;
                    s.Position = 0;

                    switch (file)
                    {
                        case PatchedFromArchive pfa:
                        {
                            var patchData = await LoadBytesFromPath(pfa.PatchID);
                            var toFile = file.To.RelativeTo(OutputFolder);
                            {
                                await using var os = await toFile.Create();
                                Utils.ApplyPatch(s, () => new MemoryStream(patchData), os);
                            }

                            if (await VirusScanner.ShouldScan(toFile) &&
                                await ClientAPI.GetVirusScanResult(toFile) == VirusScanner.Result.Malware)
                            {
                                await toFile.DeleteAsync();
                                Utils.ErrorThrow(new Exception($"Virus scan of patched executable reported possible malware: {toFile.ToString()} ({(long)await toFile.FileHashCachedAsync()})"));
                            }
                        }
                            break;

                        case FromArchive _:
                            await directive.Directive.To.RelativeTo(OutputFolder).WriteAllAsync(s, false);
                            break;
                        default:
                            throw new Exception($"No handler for {directive}");


                    }
                    
                    if (file is PatchedFromArchive)
                    {
                        await file.To.RelativeTo(OutputFolder).FileHashAsync();
                    }
                    else 
                    {
                        file.To.RelativeTo(OutputFolder).FileHashWriteCache(file.Hash);
                    }
                    
                    if (UseCompression)
                    {
                        Utils.Status($"Compacting {file.To}");
                        await file.To.RelativeTo(OutputFolder).Compact(FileCompaction.Algorithm.XPRESS16K);
                    }
                }
            });
        }

        public async Task DownloadArchives()
        {
            var missing = ModList.Archives.Where(a => !HashedArchives.ContainsKey(a.Hash)).ToList();
            Info($"Missing {missing.Count} archives");

            Info("Getting Nexus API Key, if a browser appears, please accept");

            var dispatchers = missing.Select(m => m.State.GetDownloader()).Distinct();

            await Task.WhenAll(dispatchers.Select(d => d.Prepare()));

            await DownloadMissingArchives(missing);
        }

        public async Task DownloadMissingArchives(List<Archive> missing, bool download = true)
        {
            if (download)
            {
                foreach (var a in missing.Where(a => a.State.GetType() == typeof(ManualDownloader.State)))
                {
                    var outputPath = DownloadFolder.Combine(a.Name);
                    await a.State.Download(a, outputPath);
                }
            }

            await missing.Where(a => a.State.GetType() != typeof(ManualDownloader.State))
                .PMap(Queue, async archive =>
                {
                    Info($"Downloading {archive.Name}");
                    var outputPath = DownloadFolder.Combine(archive.Name);

                    if (download)
                    {
                        if (outputPath.Exists)
                        {
                            var origName = Path.GetFileNameWithoutExtension(archive.Name);
                            var ext = Path.GetExtension(archive.Name);
                            var uniqueKey = archive.State.PrimaryKeyString.StringSha256Hex();
                            outputPath = DownloadFolder.Combine(origName + "_" + uniqueKey + "_" + ext);
                            await outputPath.DeleteAsync();
                        }
                    }

                    return await DownloadArchive(archive, download, outputPath);
                });
        }

        public async Task<bool> DownloadArchive(Archive archive, bool download, AbsolutePath? destination = null)
        {
            try
            {
                destination ??= DownloadFolder.Combine(archive.Name);
                
                var result = await DownloadDispatcher.DownloadWithPossibleUpgrade(archive, destination.Value);
                if (result == DownloadDispatcher.DownloadResult.Update)
                {
                    await destination.Value.MoveToAsync(destination.Value.Parent.Combine(archive.Hash.ToHex()));
                }
            }
            catch (Exception ex)
            {
                Utils.Log($"Download error for file {archive.Name}");
                Utils.Log(ex.ToString());
                return false;
            }

            return false;
        }

        public async Task HashArchives()
        {
            Utils.Log("Looking for files to hash");
            var toHash = DownloadFolder.EnumerateFiles()
                .Concat(Game.GameLocation().EnumerateFiles())
                .ToList();
            
            Utils.Log($"Found {toHash.Count} files to hash");
            
            var hashResults = await 
                toHash
                .PMap(Queue, async e => (await e.FileHashCachedAsync(), e)); 
            
            HashedArchives.SetTo(hashResults
                .OrderByDescending(e => e.Item2.LastModified)
                .GroupBy(e => e.Item1)
                .Select(e => e.First())
                .Select(e => new KeyValuePair<Hash, AbsolutePath>(e.Item1, e.Item2)));
        }

        /// <summary>
        /// Disabled
        /// </summary>
        public void ValidateFreeSpace()
        {
            return;
            // Disabled, caused more problems than it was worth.
            /* 
                DiskSpaceInfo DriveInfo(string path)
            {
                return Volume.GetDiskFreeSpace(Volume.GetUniqueVolumeNameForPath(path));
            }

            var paths = new[] {(OutputFolder, ModList.InstallSize),
                               (DownloadFolder, ModList.DownloadSize),
                               (Directory.GetCurrentDirectory(), ModList.ScratchSpaceSize)};
            paths.GroupBy(f => DriveInfo(f.Item1).DriveName)
                .Do(g =>
                {
                    var required = g.Sum(i => i.Item2);
                    var contains = g.Sum(folder =>
                        Directory.EnumerateFiles(folder.Item1, "*", DirectoryEnumerationOptions.Recursive)
                            .Sum(file => new FileInfo(file).Length));
                    var available = DriveInfo(g.Key).FreeBytesAvailable;
                    if (required - contains > available)
                        throw new NotEnoughDiskSpaceException(
                            $"This ModList requires {required.ToFileSizeString()} on {g.Key} but only {available.ToFileSizeString()} is available.");
                });
            */
        }


        /// <summary>
        /// The user may already have some files in the OutputFolder. If so we can go through these and
        /// figure out which need to be updated, deleted, or left alone
        /// </summary>
        public async Task OptimizeModlist()
        {
            Utils.Log("Optimizing ModList directives");
            
            // Clone the ModList so our changes don't modify the original data
            ModList = ModList.Clone();
            
            var indexed = ModList.Directives.ToDictionary(d => d.To);


            var profileFolder = OutputFolder.Combine("profiles");
            var savePath = (RelativePath)"saves";
            
            UpdateTracker.NextStep("Looking for files to delete");
            await OutputFolder.EnumerateFiles()
                .PMap(Queue, UpdateTracker, async f =>
                {
                    var relativeTo = f.RelativeTo(OutputFolder);
                    Utils.Status($"Checking if ModList file {relativeTo}");
                    if (indexed.ContainsKey(relativeTo) || f.InFolder(DownloadFolder))
                        return;

                    if (f.InFolder(profileFolder) && f.Parent.FileName == savePath) return;

                    Utils.Log($"Deleting {relativeTo} it's not part of this ModList");
                    await f.DeleteAsync();
                });

            Utils.Log("Cleaning empty folders");
            var expectedFolders = indexed.Keys
                .Select(f => f.RelativeTo(OutputFolder))
                // We ignore the last part of the path, so we need a dummy file name
                .Append(DownloadFolder.Combine("_"))
                .Where(f => f.InFolder(OutputFolder))
                .SelectMany(path =>
                {
                    // Get all the folders and all the folder parents
                    // so for foo\bar\baz\qux.txt this emits ["foo", "foo\\bar", "foo\\bar\\baz"]
                    var split = ((string)path.RelativeTo(OutputFolder)).Split('\\');
                    return Enumerable.Range(1, split.Length - 1).Select(t => string.Join("\\", split.Take(t)));
                })
               .Distinct()
                .Select(p => OutputFolder.Combine(p))
               .ToHashSet();

            try
            {
                var toDelete = OutputFolder.EnumerateDirectories(true)
                    .Where(p => !expectedFolders.Contains(p))
                    .OrderByDescending(p => ((string)p).Length)
                    .ToList();
                foreach (var dir in toDelete)
                {
                    await dir.DeleteDirectory(dontDeleteIfNotEmpty:true);
                }
            }
            catch (Exception)
            {
                // ignored because it's not worth throwing a fit over
                Utils.Log("Error when trying to clean empty folders. This doesn't really matter.");
            }

            UpdateTracker.NextStep("Looking for unmodified files");
            (await indexed.Values.PMap(Queue, UpdateTracker, async d =>
            {
                // Bit backwards, but we want to return null for 
                // all files we *want* installed. We return the files
                // to remove from the install list.
                Status($"Optimizing {d.To}");
                var path = OutputFolder.Combine(d.To);
                if (!path.Exists) return null;

                if (path.Size != d.Size) return null;
                
                return await path.FileHashCachedAsync() == d.Hash ? d : null;
            }))
              .Do(d =>
              {
                  if (d != null)
                  {
                      indexed.Remove(d.To);
                  }
              });

            UpdateTracker.NextStep("Updating ModList");
            Utils.Log($"Optimized {ModList.Directives.Count} directives to {indexed.Count} required");
            var requiredArchives = indexed.Values.OfType<FromArchive>()
                .GroupBy(d => d.ArchiveHashPath.BaseHash)
                .Select(d => d.Key)
                .ToHashSet();
            
            ModList.Archives = ModList.Archives.Where(a => requiredArchives.Contains(a.Hash)).ToList();
            ModList.Directives = indexed.Values.ToList();

        }
    }

    public class NotEnoughDiskSpaceException : Exception
    {
        public NotEnoughDiskSpaceException(string s) : base(s)
        {
        }
    }
}
