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
        // ---------- free-plan limits ----------
        private const int MAX_LABELS_PER_CALL = 50;
        private const int MAX_BYTES_PER_CALL = 900 * 1024; // keep a margin below 1 MB
        private static readonly TimeSpan MIN_DELAY_BETWEEN_CALLS = TimeSpan.FromMilliseconds(350); //  ≈2.8 req/s 

        // ---------- HttpClient ----------
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
                    FileName = fullPath,   // caminho do PDF
                    UseShellExecute = true,       // <-- necessário no .NET Core/5/6/7/8
                    Verb = "open"      // opcional, deixa explícito
                };
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Labelary] could not open : {ex.Message}");
            }
        }

        // ---------- public façade ----------
        public static async Task SendTo(string zplCode)
        {
            Console.WriteLine($"[Labelary] total raw length = {zplCode.Length:N0} chars");
            var pdfs = await RenderAsync(zplCode).ConfigureAwait(false);

            Console.WriteLine($"[Labelary] finished – PDF(s) saved: {pdfs.Count}");
            foreach (var path in pdfs)
                OpenPdf(path);
        }

        // ---------- pipeline ----------
        private static async Task<List<string>> RenderAsync(string allZpl, CancellationToken ct = default)
        {
            var labels = SplitLabels(allZpl);
            Console.WriteLine($"[Labelary] found {labels.Count} ^XA labels");

            var batches = BuildBatches(labels).ToList();
            Console.WriteLine($"[Labelary] batching into {batches.Count} call(s)");

            var saved = new List<string>();
            int idx = 1;

            // ensure files are stored in a writable folder
            string outDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "labels");
            Directory.CreateDirectory(outDir);

            foreach (var batch in batches)
            {
                Console.WriteLine($"[Labelary] ▶ batch {idx} – {batch.Length:N0} chars");

                byte[] pdf = await SendBatchAsync(batch, ct);

                string file = Path.Combine(outDir, $"{DateForSave()}-label-{idx:D3}.pdf");
                File.WriteAllBytes(file, pdf);
                Console.WriteLine($"[Labelary]   ✓ received {pdf.Length:N0} bytes → {file}");
                saved.Add(file);
                idx++;
            }

            return saved;
        }
        private static List<string> SplitLabels(string bigZpl) =>
                Regex.Split(bigZpl, @"(?=\^XA)", RegexOptions.Multiline)
                 .Where(s => !string.IsNullOrWhiteSpace(s))
                  .ToList();
        private static IEnumerable<string> BuildBatches(IEnumerable<string> labels)
        {
            var sb = new StringBuilder();
            int lblCount = 0;
            int byteCount = 0;

            foreach (var lbl in labels)
            {
                int bytes = Encoding.ASCII.GetByteCount(lbl);

                bool full = lblCount == MAX_LABELS_PER_CALL ||
                            byteCount + bytes > MAX_BYTES_PER_CALL;

                if (full)
                {
                    yield return sb.ToString();
                    sb.Clear();
                    lblCount = 0;
                    byteCount = 0;
                }

                sb.Append(lbl);
                lblCount++;
                byteCount += bytes;
            }

            if (lblCount > 0)
                yield return sb.ToString();
        }
        private static async Task<byte[]> SendBatchAsync(string zplBatch, CancellationToken ct = default(CancellationToken))
        {
            var wait = _nextSlotUtc - DateTime.UtcNow;
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait, ct);
            _nextSlotUtc = DateTime.UtcNow + MIN_DELAY_BETWEEN_CALLS;

            byte[] payload = Encoding.UTF8.GetBytes(zplBatch);
            var content = new ByteArrayContent(payload);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");

            var request = new HttpRequestMessage(HttpMethod.Post,
                "https://api.labelary.com/v1/printers/8dpmm/labels/4x6/")
            {
                Content = content
            };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/pdf"));

            Console.WriteLine("[Labelary]   → POST " + payload.Length.ToString("N0") + " bytes");

            var resp = await _http.SendAsync(request,
                                             HttpCompletionOption.ResponseHeadersRead,
                                             ct);

            Console.WriteLine("[Labelary]   ← " + ((int)resp.StatusCode) + " " + resp.ReasonPhrase);

            if (!resp.IsSuccessStatusCode)
            {
                string err = await resp.Content.ReadAsStringAsync();
                Console.WriteLine("[Labelary]     body: " + err);
                throw new Exception("Labelary falhou (" + (int)resp.StatusCode + "): " + resp.ReasonPhrase);
            }

            var bytes = await resp.Content.ReadAsByteArrayAsync();
            Console.WriteLine("[Labelary]   ✓ OK " + bytes.Length.ToString("N0") + " bytes");
            return bytes;
        }
        public static string DateForSave()
        {
            var dateTime = DateTime.Now.ToString("dd-MM-yyyy");

            return dateTime;
        }
    }
}
