using System;
using System.Collections.Generic;
using System.Globalization;

namespace DriveFolderSync
{
    // Tracks bytes copied by a running sync so the window, tray and shutdown screen can show
    // amount, speed and time left. Written by the sync thread and read by the UI thread.
    public sealed class SyncProgress
    {
        readonly object gate = new object();
        readonly Queue<KeyValuePair<DateTime, long>> samples = new Queue<KeyValuePair<DateTime, long>>();
        readonly Dictionary<string, long> activeTransfers = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        string direction = "";
        string folder = "";
        string current = "";
        int folderIndex;
        int folderCount;
        int totalFiles;
        int doneFiles;
        long totalBytes;
        long doneBytes;
        long fileBase;
        long moved;
        long movedBase;
        DateTime startedUtc = DateTime.UtcNow;
        string checkStatus = "";
        double checkEtaSeconds = 0;
        int checkCurrent = -1;
        int checkTotal = -1;
        bool planned;
        volatile bool canceled;

        public SyncProgress(string direction)
        {
            this.direction = direction ?? "";
        }

        public int TotalFiles { get { lock (gate) return totalFiles; } }
        public int DoneFiles { get { lock (gate) return doneFiles; } }
        public long TotalBytes { get { lock (gate) return totalBytes; } }
        public long DoneBytes { get { lock (gate) return doneBytes; } }
        public string CurrentFile { get { lock (gate) return current; } }
        public string Folder { get { lock (gate) return folder; } }
        public bool Canceled { get { return canceled; } }
        public DateTime StartedUtc { get { lock (gate) return startedUtc; } }
        public string CheckStatus { get { lock (gate) return checkStatus; } }
        public double CheckEtaSeconds { get { lock (gate) return checkEtaSeconds; } }
        public int CheckCurrent { get { lock (gate) return checkCurrent; } }
        public int CheckTotal { get { lock (gate) return checkTotal; } }

        public int CheckPercent
        {
            get
            {
                lock (gate)
                {
                    if (checkTotal <= 0 || checkCurrent < 0) return -1;
                    return (int)Math.Min(100, Math.Max(0, 100L * checkCurrent / checkTotal));
                }
            }
        }

        public void SetCheckStatus(string status)
        {
            SetCheckStatus(status, null, -1, -1);
        }

        public void SetCheckStatus(string status, double? etaSeconds)
        {
            SetCheckStatus(status, etaSeconds, -1, -1);
        }

        public void SetCheckStatus(string status, double? etaSeconds, int currentItem, int totalItems)
        {
            lock (gate)
            {
                checkStatus = status ?? "";
                checkEtaSeconds = etaSeconds.HasValue && etaSeconds.Value > 0 ? etaSeconds.Value : 0;
                checkCurrent = currentItem;
                checkTotal = totalItems;
            }
        }

        // Stops after the current file; files already copied stay copied.
        public void Cancel()
        {
            canceled = true;
        }

        // Percent of the current folder's bytes, or -1 while the folder is still being compared.
        public int Percent
        {
            get
            {
                lock (gate)
                {
                    if (!planned) return -1;
                    if (totalBytes <= 0) return totalFiles == 0 ? 100 : (int)(100L * doneFiles / totalFiles);
                    return (int)Math.Min(100, 100L * doneBytes / totalBytes);
                }
            }
        }

        internal void StartFolder(string path, int index, int count)
        {
            lock (gate)
            {
                folder = path ?? "";
                folderIndex = index;
                folderCount = count;
                totalFiles = 0;
                doneFiles = 0;
                totalBytes = 0;
                doneBytes = 0;
                fileBase = 0;
                current = "";
                planned = false;
                checkStatus = "";
                checkEtaSeconds = 0;
                checkCurrent = -1;
                checkTotal = -1;
                startedUtc = DateTime.UtcNow;
                samples.Clear();
                activeTransfers.Clear();
            }
        }

        internal void Plan(int files, long bytes)
        {
            lock (gate)
            {
                totalFiles = files;
                totalBytes = bytes;
                planned = true;
                Sample(DateTime.UtcNow);
            }
        }

        internal void BeginFile(string rel)
        {
            lock (gate)
            {
                string key = rel ?? "";
                activeTransfers[key] = 0;
                current = key;
            }
        }

        internal void Copied(long transferred)
        {
            lock (gate)
            {
                Copied(current, transferred);
            }
        }

        internal void Copied(string rel, long transferred)
        {
            lock (gate)
            {
                string key = string.IsNullOrEmpty(rel) ? (current ?? "") : rel;
                activeTransfers[key] = transferred;
                long inFlight = 0;
                foreach (long val in activeTransfers.Values) inFlight += val;
                doneBytes = fileBase + inFlight;
                moved = movedBase + inFlight;
                Sample(DateTime.UtcNow);
            }
        }

        internal void EndFile(long bytes, bool copied)
        {
            lock (gate)
            {
                EndFile(current, bytes, copied);
            }
        }

        internal void EndFile(string rel, long bytes, bool copied)
        {
            lock (gate)
            {
                string key = string.IsNullOrEmpty(rel) ? (current ?? "") : rel;
                activeTransfers.Remove(key);
                fileBase += bytes;
                if (copied) movedBase += bytes;
                long inFlight = 0;
                foreach (long val in activeTransfers.Values) inFlight += val;
                doneBytes = fileBase + inFlight;
                moved = movedBase + inFlight;
                doneFiles++;
                if (current == key)
                {
                    current = "";
                    foreach (string active in activeTransfers.Keys) { current = active; break; }
                }
                Sample(DateTime.UtcNow);
            }
        }

        void Sample(DateTime now)
        {
            if (samples.Count > 0)
            {
                KeyValuePair<DateTime, long> last = default(KeyValuePair<DateTime, long>);
                foreach (KeyValuePair<DateTime, long> item in samples) last = item;
                if ((now - last.Key).TotalMilliseconds < 250) return;
            }
            samples.Enqueue(new KeyValuePair<DateTime, long>(now, moved));
            while (samples.Count > 2 && (now - samples.Peek().Key).TotalSeconds > 10) samples.Dequeue();
        }

        // Average bytes per second over the last few seconds, or 0 before there is enough data.
        public double BytesPerSecond()
        {
            return BytesPerSecond(DateTime.UtcNow);
        }

        internal double BytesPerSecond(DateTime now)
        {
            lock (gate)
            {
                if (samples.Count == 0) return 0;
                KeyValuePair<DateTime, long> first = samples.Peek();
                double seconds = (now - first.Key).TotalSeconds;
                if (seconds < 1)
                {
                    double totalSec = (now - startedUtc).TotalSeconds;
                    if (totalSec >= 1 && moved > 0) return moved / totalSec;
                    return 0;
                }
                return Math.Max(0, moved - first.Value) / seconds;
            }
        }

        public static string FormatElapsed(TimeSpan duration)
        {
            if (duration < TimeSpan.Zero) duration = TimeSpan.Zero;
            int hours = (int)duration.TotalHours;
            int minutes = duration.Minutes;
            int seconds = duration.Seconds;
            if (hours > 0)
                return string.Format(CultureInfo.InvariantCulture, "{0}:{1:D2}:{2:D2}", hours, minutes, seconds);
            return string.Format(CultureInfo.InvariantCulture, "{0}:{1:D2}", minutes, seconds);
        }

        public string Describe()
        {
            return Describe(DateTime.UtcNow);
        }

        internal string Describe(DateTime now)
        {
            double speed = BytesPerSecond(now);
            lock (gate)
            {
                string name = folder.TrimEnd('\\');
                int slash = name.LastIndexOf('\\');
                if (slash >= 0 && slash < name.Length - 1) name = name.Substring(slash + 1);
                if (folderCount > 1) name += " (" + folderIndex + " of " + folderCount + ")";
                string verb = direction == "Pull" ? "Refreshing" : "Backing up";
                if (canceled) return "Stopping after the current file…";

                string elapsed = FormatElapsed(now - startedUtc) + " elapsed";
                if (!planned)
                {
                    List<string> checkParts = new List<string>();
                    string title = "Checking " + name;
                    if (!string.IsNullOrEmpty(checkStatus)) title += " (" + checkStatus + ")";
                    checkParts.Add(title);
                    if (checkEtaSeconds > 0) checkParts.Add("ETA " + TimeLeft(checkEtaSeconds));
                    checkParts.Add(elapsed);
                    return string.Join("  ·  ", checkParts.ToArray());
                }

                if (totalFiles == 0) return verb + " " + name + "  ·  nothing to copy  ·  " + elapsed;
                List<string> parts = new List<string>();
                parts.Add(verb + " " + name);
                parts.Add(SyncEngine.FormatSize(doneBytes) + " of " + SyncEngine.FormatSize(totalBytes));
                if (speed > 0) parts.Add(SyncEngine.FormatSize((long)speed) + "/s");
                long remaining = Math.Max(0, totalBytes - doneBytes);
                if (speed > 0 && remaining > 0) parts.Add("ETA " + TimeLeft(remaining / speed));
                parts.Add(elapsed);
                parts.Add("file " + Math.Min(totalFiles, doneFiles + 1) + " of " + totalFiles);
                return string.Join("  ·  ", parts.ToArray());
            }
        }

        // Short form for the tray tooltip, which Windows limits to 63 characters.
        public string Brief()
        {
            double speed = BytesPerSecond();
            lock (gate)
            {
                string verb = direction == "Pull" ? "Refreshing" : "Backing up";
                string elapsed = FormatElapsed(DateTime.UtcNow - startedUtc);
                if (!planned)
                {
                    if (checkEtaSeconds > 0)
                        return verb + " \u00b7 ETA " + TimeLeft(checkEtaSeconds) + " \u00b7 " + elapsed;
                    return verb + "\u2026 \u00b7 " + elapsed;
                }
                long remaining = Math.Max(0, totalBytes - doneBytes);
                string eta = (speed > 0 && remaining > 0) ? "ETA " + TimeLeft(remaining / speed) : null;
                string text = verb + " " + Percent + "%";
                if (eta != null) text += " \u00b7 " + eta;
                text += " \u00b7 " + elapsed;
                if (text.Length > 44 && eta != null)
                {
                    text = verb + " " + Percent + "% \u00b7 " + TimeLeft(remaining / speed) + " \u00b7 " + elapsed;
                }
                return text;
            }
        }

        public static string TimeLeft(double seconds)
        {
            if (seconds < 5) return "a few seconds left";
            if (seconds < 60) return "about " + (int)Math.Ceiling(seconds / 5) * 5 + " s left";
            int minutes = (int)Math.Ceiling(seconds / 60);
            if (minutes < 60) return "about " + minutes + " min left";
            return "about " + (minutes / 60) + " h " + (minutes % 60).ToString(CultureInfo.InvariantCulture) + " min left";
        }
    }
}
