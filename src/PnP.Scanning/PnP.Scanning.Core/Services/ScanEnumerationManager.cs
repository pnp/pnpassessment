using Google.Protobuf.WellKnownTypes;
using PnP.Scanning.Core.Storage;
using Serilog;
using System.Text.RegularExpressions;

namespace PnP.Scanning.Core.Services
{
    internal static class ScanEnumerationManager
    {
        internal static async Task<ListReply> EnumerateScansFromDiskAsync(StorageManager storageManager, bool running, bool paused, bool finished, bool terminated)
        {
            ListReply scans = new();

            // List folders that possibly contain a scan result
            Regex regex = new("[({]?[a-fA-F0-9]{8}[-]?([a-fA-F0-9]{4}[-]?){3}[a-fA-F0-9]{12}[})]?", RegexOptions.IgnoreCase);
            var scanFolders = Directory.EnumerateDirectories(StorageManager.GetScannerFolder()).Where(f => regex.IsMatch(f));

            List<ListScanResponse> tempResults = new();

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
                        scanResult.Mode = scanResultFromDatabase.Mode;
                        scanResult.ScanStarted = Timestamp.FromDateTime(scanResultFromDatabase.StartDate);
                        scanResult.ScanEnded = Timestamp.FromDateTime(scanResultFromDatabase.EndDate);
                        scanResult.SiteCollectionsToScan = scanResultFromDatabase.SiteCollectionsToScan;
                        scanResult.SiteCollectionsScanned = scanResultFromDatabase.SiteCollectionsFinished + scanResultFromDatabase.SiteCollectionsFailed;

                        bool add = false;
                        if (running && scanResultFromDatabase.Status == ScanStatus.Running)
                        {
                            add = true;
                        }
                        else if (paused && (scanResultFromDatabase.Status == ScanStatus.Queued || scanResultFromDatabase.Status == ScanStatus.Paused || scanResultFromDatabase.Status == ScanStatus.Pausing))
                        {
                            add = true;
                        }
                        else if (finished && scanResultFromDatabase.Status == ScanStatus.Finished)
                        {
                            add = true;
                        }
                        else if (terminated && scanResultFromDatabase.Status == ScanStatus.Terminated)
                        {
                            add = true;
                        }

                        if (add)
                        {
                            tempResults.Add(scanResult);
                        }
                    }
                }
                else
                {
                    Log.Warning("Assessment result folder {Folder} was skipped as there's no assessment database in the folder", scan);
                }
            }

            if (tempResults.Any())
            {
                foreach (var scanResult in tempResults.Where(p => p.Status == ScanStatus.Finished.ToString()).OrderBy(p => p.ScanStarted))
                {
                    scans.Status.Add(scanResult);
                }
                foreach (var scanResult in tempResults.Where(p => p.Status == ScanStatus.Terminated.ToString()).OrderBy(p => p.ScanStarted))
                {
                    scans.Status.Add(scanResult);
                }
                foreach (var scanResult in tempResults.Where(p => p.Status == ScanStatus.Paused.ToString()).OrderBy(p => p.ScanStarted))
                {
                    scans.Status.Add(scanResult);
                }
                foreach (var scanResult in tempResults.Where(p => p.Status == ScanStatus.Pausing.ToString()).OrderBy(p => p.ScanStarted))
                {
                    scans.Status.Add(scanResult);
                }
                foreach (var scanResult in tempResults.Where(p => p.Status == ScanStatus.Running.ToString()).OrderBy(p => p.ScanStarted))
                {
                    scans.Status.Add(scanResult);
                }
                foreach (var scanResult in tempResults.Where(p => p.Status == ScanStatus.Queued.ToString()).OrderBy(p=>p.ScanStarted))
                {
                    scans.Status.Add(scanResult);
                }
            }

            return scans;
        }
    }
}
