// Form1.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression; // needs reference to System.IO.Compression.FileSystem (Framework 4.8)
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace NET_Labelary
{
    public partial class Form1 : Form
    {
        readonly string _extractRoot;

        public Form1()
        {
            InitializeComponent();
            this.AllowDrop = true;
            this.DragEnter += textBox1_DragEnter;
            this.DragDrop += textBox1_DragDrop;
            Console.SetOut(new TextBoxWriter(textBox1));
            _extractRoot = Path.Combine(Path.GetTempPath(), "NET_Labelary_Extract");
            Directory.CreateDirectory(_extractRoot);
        }

        async void textBox1_DragDrop(object sender, DragEventArgs e)
        {
            try
            {
                if (e.Data == null || !e.Data.GetDataPresent(DataFormats.FileDrop))
                    return;

                var files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (files == null || files.Length == 0)
                    return;

                var allTxtFiles = new List<string>();
                var sbLog = new StringBuilder();

                foreach (var file in files)
                {
                    if (string.IsNullOrWhiteSpace(file))
                        continue;

                    var ext = Path.GetExtension(file);
                    if (ext == null)
                        continue;

                    if (ext.Equals(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        sbLog.AppendLine("=== ZIP: " + file + " ===");
                        var extractedDir = ExtractZipToUniqueFolder(file);
                        if (string.IsNullOrEmpty(extractedDir))
                        {
                            sbLog.AppendLine("  (failed to extract)");
                            sbLog.AppendLine();
                            continue;
                        }

                        sbLog.AppendLine("Extracted to: " + extractedDir);

                        string[] txtFiles;
                        try
                        {
                            txtFiles = Directory.GetFiles(extractedDir, "*.txt", SearchOption.AllDirectories);
                        }
                        catch
                        {
                            txtFiles = Array.Empty<string>();
                        }

                        if (txtFiles.Length == 0)
                        {
                            sbLog.AppendLine("  (no .txt files found)");
                        }
                        else
                        {
                            foreach (var txt in txtFiles)
                            {
                                sbLog.AppendLine("  " + txt);
                                allTxtFiles.Add(txt);
                            }
                        }

                        sbLog.AppendLine();
                    }
                    else if (ext.Equals(".txt", StringComparison.OrdinalIgnoreCase))
                    {
                        sbLog.AppendLine("=== TXT: " + file + " ===");
                        sbLog.AppendLine("  (direct file)");
                        sbLog.AppendLine();
                        allTxtFiles.Add(file);
                    }
                }

                if (sbLog.Length > 0)
                    Console.WriteLine(sbLog.ToString());

                // send each .txt as ZPL to Labelary
                foreach (var txtPath in allTxtFiles)
                {
                    string zplCode;
                    try
                    {
                        zplCode = File.ReadAllText(txtPath, Encoding.UTF8);
                    }
                    catch (Exception exRead)
                    {
                        Console.WriteLine("[Labelary] failed to read: " + txtPath + " -> " + exRead.Message);
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(zplCode))
                    {
                        Console.WriteLine("[Labelary] empty file, skipping: " + txtPath);
                        continue;
                    }

                    try
                    {
                        await Labelary.SendTo(zplCode).ConfigureAwait(true);
                    }
                    catch (Exception exSend)
                    {
                        Console.WriteLine("[Labelary] SendTo failed for: " + txtPath + " -> " + exSend.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        void textBox1_DragEnter(object sender, DragEventArgs e)
        {
            if (e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (files != null && files.Length > 0)
                {
                    foreach (var f in files)
                    {
                        if (string.IsNullOrWhiteSpace(f))
                            continue;

                        var ext = Path.GetExtension(f);
                        if (ext == null)
                            continue;

                        if (ext.Equals(".zip", StringComparison.OrdinalIgnoreCase) ||
                            ext.Equals(".txt", StringComparison.OrdinalIgnoreCase))
                        {
                            e.Effect = DragDropEffects.Copy;
                            return;
                        }
                    }
                }
            }

            e.Effect = DragDropEffects.None;
        }

        string ExtractZipToUniqueFolder(string zipPath)
        {
            try
            {
                var baseName = Path.GetFileNameWithoutExtension(zipPath);
                var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
                var targetDir = Path.Combine(_extractRoot, baseName + "_" + stamp);

                Directory.CreateDirectory(targetDir);
                ZipFile.ExtractToDirectory(zipPath, targetDir);
                return targetDir;
            }
            catch (Exception ex)
            {
                Console.WriteLine("[ZipExtract] failed: " + ex.Message);
                return null;
            }
        }
    }
}
