using Google.Protobuf.WellKnownTypes;
using PnP.Scanning.Core.Storage;
using Serilog;
using System.Text.RegularExpressions;

namespace PnP.Scanning.Core.Services
{
    internal static class ScanEnumerationManager
    {
        internal static async Task<ListReply> EnumerateScansFromDiskAsync(StorageManager storageManager, bool running, bool paused, bool finished, bool failed)
        {
            ListReply scans = new();

            // List folders that possibly contain a scan result
            Regex regex = new("[({]?[a-fA-F0-9]{8}[-]?([a-fA-F0-9]{4}[-]?){3}[a-fA-F0-9]{12}[})]?", RegexOptions.IgnoreCase);
            var scanFolders = Directory.EnumerateDirectories(StorageManager.GetScannerFolder()).Where(f => regex.IsMatch(f));

            foreach (var scan in scanFolders)
            {
                string scanIdString = scan.Substring(scan.LastIndexOf(Path.DirectorySeparatorChar) + 1);
                Guid scanId = Guid.Parse(scanIdString);
                var scanResult = new ListScanResponse
                {
                    Id = scanIdString,
                };

                if (File.Exists(Path.Combine(scan, StorageManager.DbName)))
                {
                    // Get status from the scan database
                    var scanResultFromDatabase = await storageManager.GetScanResultAsync(scanId);
                    if (scanResultFromDatabase != null)
                    {
                        scanResult.Status = scanResultFromDatabase.Status.ToString();
                        scanResult.ScanStarted = Timestamp.FromDateTime(scanResultFromDatabase.StartDate);
                        scanResult.ScanEnded = Timestamp.FromDateTime(scanResultFromDatabase.EndDate);
                        scanResult.SiteCollectionsToScan = scanResultFromDatabase.SiteCollectionsToScan;
                        scanResult.SiteCollectionsScanned = scanResultFromDatabase.SiteCollectionsFinished + scanResultFromDatabase.SiteCollectionsFailed;

                        bool add = false;
                        if (running && scanResultFromDatabase.Status == Storage.ScanStatus.Running)
                        {
                            add = true;
                        }
                        else if (paused && scanResultFromDatabase.Status == Storage.ScanStatus.Paused)
                        {
                            add = true;
                        }
                        else if (finished && scanResultFromDatabase.Status == Storage.ScanStatus.Finished)
                        {
                            add = true;
                        }
                        else if (failed && scanResultFromDatabase.Status == Storage.ScanStatus.Failed)
                        {
                            add = true;
                        }

                        if (add)
                        {
                            scans.Status.Add(scanResult);
                        }
                    }
                }
                else
                {
                    Log.Warning("Scan result folder {Folder} was skipped as there's no scan database in the folder", scan);
                }
            }

            return scans;
        }
    }
}
