using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace NET_Labelary
{
    internal static class Program
    {
        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            SetGlobalFont(new Font("Segoe UI", 8));
            Application.Run(new Form1());
        }


        static void SetGlobalFont(Font font)
        {
            void ApplyFont(Control control)
            {
                control.Font = font;
                foreach (Control child in control.Controls)
                    ApplyFont(child);
            }

            Application.ApplicationExit += (s, e) => font.Dispose();

            Application.Idle += (s, e) =>
            {
                if (Application.OpenForms.Count > 0)
                {
                    foreach (Form form in Application.OpenForms)
                        ApplyFont(form);
                    Application.Idle -= (s2, e2) => { };
                }
            };
        }

    }

    // Redirects Console.Write/WriteLine to a TextBox.
    public class TextBoxWriter : TextWriter
    {
        private readonly TextBox _output;

        public TextBoxWriter(TextBox output)
        {
            _output = output;
        }

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            if (_output.IsDisposed) return;

            if (_output.InvokeRequired)
                _output.Invoke(new Action<char>(Write), value);
            else
                _output.AppendText(value.ToString());
        }

        public override void Write(string value)
        {
            if (value == null) return;
            if (_output.IsDisposed) return;

            if (_output.InvokeRequired)
                _output.Invoke(new Action<string>(Write), value);
            else
                _output.AppendText(value);
        }

        public override void WriteLine(string value)
        {
            Write(value + Environment.NewLine);
        }
    }
}

