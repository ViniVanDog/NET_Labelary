using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace NET_Labelary
{
    public static class Labelary
    {
        private const int MAX_LABELS_PER_CALL = 50;
        private const int MAX_BYTES_PER_CALL = 900 * 1024;              // request body limit (keep margin under 1MB)
        private const int MAX_EMBEDDED_IMAGE_BYTES_PER_CALL = 1_800_000; // heuristic < 2MB fonts+images
        private static readonly TimeSpan MIN_DELAY_BETWEEN_CALLS = TimeSpan.FromMilliseconds(350);

        private static readonly HttpClient _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        private static DateTime _nextSlotUtc = DateTime.UtcNow;

        static void OpenPdf(string fullPath)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = fullPath,
                    UseShellExecute = true,
                    Verb = "open"
                };
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[Labelary] could not open : " + ex.Message);
            }
        }
        public static async Task SendTo(string zplCode)
        {
            Console.WriteLine("[Labelary] total raw length = " + zplCode.Length.ToString("N0") + " chars");
            var pdfs = await RenderAsync(zplCode).ConfigureAwait(false);

            Console.WriteLine("[Labelary] finished – PDF(s) saved: " + pdfs.Count);
            foreach (var path in pdfs)
                OpenPdf(path);
        }

        private static async Task<List<string>> RenderAsync(string allZpl, CancellationToken ct = default(CancellationToken))
        {
            var labels = SplitLabels(allZpl);
            Console.WriteLine("[Labelary] found " + labels.Count + " ^XA labels");

            var saved = new List<string>();
            int batchIndex = 1;
            int i = 0;

            string outDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "labels");
            Directory.CreateDirectory(outDir);

            while (i < labels.Count)
            {
                string batchZpl;
                int labelsUsed;
                int byteCount;
                int imageBytes;

                BuildBatchFromIndex(labels, i, out batchZpl, out labelsUsed, out byteCount, out imageBytes);

                if (labelsUsed == 0)
                {
                    batchZpl = labels[i];
                    labelsUsed = 1;
                    byteCount = Encoding.ASCII.GetByteCount(batchZpl);
                    imageBytes = EstimateEmbeddedImageBytes(batchZpl);
                }

                Console.WriteLine(
                    "[Labelary] ▶ batch " + batchIndex +
                    " – labels " + labelsUsed +
                    ", bytes " + byteCount.ToString("N0") +
                    ", embedded image bytes ~ " + imageBytes.ToString("N0"));

                byte[] pdf = await SendBatchAsync(batchZpl, ct).ConfigureAwait(false);

                string file = Path.Combine(outDir,
                    DateForSave() + "-label-" + batchIndex.ToString("D3") + ".pdf");

                File.WriteAllBytes(file, pdf);
                Console.WriteLine("[Labelary]   ✓ received " + pdf.Length.ToString("N0") + " bytes → " + file);
                saved.Add(file);

                i += labelsUsed;
                batchIndex++;
            }

            return saved;
        }

        // keeps full ZPL sequence but ensures each "label" starts with ^XA
        private static List<string> SplitLabels(string bigZpl)
        {
            var parts = Regex.Split(bigZpl, @"(?=\^XA)", RegexOptions.Multiline);
            var labels = new List<string>();
            var carry = new StringBuilder();

            foreach (var raw in parts)
            {
                var part = raw;
                if (string.IsNullOrWhiteSpace(part))
                    continue;

                if (part.StartsWith("^XA", StringComparison.Ordinal))
                {
                    if (carry.Length > 0)
                    {
                        if (labels.Count > 0)
                        {
                            labels[labels.Count - 1] += carry.ToString();
                        }
                        else
                        {
                            part = carry.ToString() + part;
                        }
                        carry.Clear();
                    }

                    labels.Add(part);
                }
                else
                {
                    carry.Append(part);
                }
            }

            if (carry.Length > 0 && labels.Count > 0)
            {
                labels[labels.Count - 1] += carry.ToString();
            }

            return labels;
        }

        // batches by: label count, request size, and estimated embedded image weight (~DG/^GF)
        private static void BuildBatchFromIndex(
            IList<string> labels,
            int startIndex,
            out string zplBatch,
            out int labelsUsed,
            out int byteCount,
            out int imageBytes)
        {
            var sb = new StringBuilder();
            labelsUsed = 0;
            byteCount = 0;
            imageBytes = 0;

            int hardLimit = MAX_LABELS_PER_CALL;

            for (int offset = 0; offset < hardLimit && (startIndex + offset) < labels.Count; offset++)
            {
                string lbl = labels[startIndex + offset];
                int bytes = Encoding.ASCII.GetByteCount(lbl);
                int imgBytes = EstimateEmbeddedImageBytes(lbl);

                bool wouldOverflowBody = labelsUsed > 0 && (byteCount + bytes > MAX_BYTES_PER_CALL);
                bool wouldOverflowImages = labelsUsed > 0 && (imageBytes + imgBytes > MAX_EMBEDDED_IMAGE_BYTES_PER_CALL);

                if (wouldOverflowBody || wouldOverflowImages)
                    break;

                sb.Append(lbl);
                labelsUsed++;
                byteCount += bytes;
                imageBytes += imgBytes;
            }

            zplBatch = sb.ToString();
        }

        // estimate embedded image bytes from ~DG / ~DGR and ^GF commands
        private static int EstimateEmbeddedImageBytes(string zpl)
        {
            if (string.IsNullOrEmpty(zpl))
                return 0;

            int total = 0;

            // ~DG / ~DGR: ~DGR:NAME.GRF,124236,102,:Z64:...
            var dgMatches = Regex.Matches(
                zpl,
                @"~DG[RF]?:[^,]*,(\d+),",
                RegexOptions.IgnoreCase);

            foreach (Match m in dgMatches)
            {
                int val;
                if (int.TryParse(m.Groups[1].Value, out val))
                    total += val;
            }

            // ^GFo,h,w,data (h = total bytes of graphic data)
            int idx = 0;
            while (true)
            {
                idx = zpl.IndexOf("^GF", idx, StringComparison.OrdinalIgnoreCase);
                if (idx < 0)
                    break;

                int fs = zpl.IndexOf("^FS", idx + 3, StringComparison.OrdinalIgnoreCase);
                if (fs < 0)
                    fs = zpl.Length;

                string field = zpl.Substring(idx, fs - idx);
                string[] parts = field.Split(',');

                if (parts.Length >= 3)
                {
                    // parts[0]: "^GFo" (orientation included)
                    // parts[1]: h (total bytes) – may have non-digits, strip them
                    string raw = parts[1];
                    var sb = new StringBuilder();
                    for (int i = 0; i < raw.Length; i++)
                    {
                        char c = raw[i];
                        if (char.IsDigit(c))
                            sb.Append(c);
                    }

                    int val;
                    if (sb.Length > 0 && int.TryParse(sb.ToString(), out val))
                        total += val;
                }

                idx = fs;
            }

            return total;
        }

        private static async Task<byte[]> SendBatchAsync(string zplBatch, CancellationToken ct = default(CancellationToken))
        {
            TimeSpan wait = _nextSlotUtc - DateTime.UtcNow;
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait, ct).ConfigureAwait(false);

            _nextSlotUtc = DateTime.UtcNow + MIN_DELAY_BETWEEN_CALLS;

            byte[] payload = Encoding.UTF8.GetBytes(zplBatch);
            var content = new ByteArrayContent(payload);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");

            var request = new HttpRequestMessage(
                HttpMethod.Post,
                "https://api.labelary.com/v1/printers/8dpmm/labels/4x6/")
            {
                Content = content
            };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/pdf"));

            Console.WriteLine("[Labelary]   → POST " + payload.Length.ToString("N0") + " bytes");

            HttpResponseMessage resp = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            Console.WriteLine("[Labelary]   ← " + (int)resp.StatusCode + " " + resp.ReasonPhrase);

            if (!resp.IsSuccessStatusCode)
            {
                string err = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                Console.WriteLine("[Labelary]     body: " + err);
                throw new Exception("Labelary failed (" + (int)resp.StatusCode + "): " + resp.ReasonPhrase);
            }

            byte[] bytes = await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            Console.WriteLine("[Labelary]   ✓ OK " + bytes.Length.ToString("N0") + " bytes");
            return bytes;
        }

        public static string DateForSave()
        {
            return DateTime.Now.ToString("dd-MM-yyyy");
        }
    }
}
