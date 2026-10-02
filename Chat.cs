using System;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ChatApp
{
    public class ChatForm : Form
    {
        private RichTextBox rtbLog = null!;
        private TextBox txtMsg = null!;
        private Button btnSend = null!;
        private Button btnSendFile = null!;
        private TextBox txtDestPort = null!;
        private ComboBox cbDestIp = null!;
        private TextBox txtListeningPort = null!;
        private TextBox txtNickname = null!;
        private Button btnChangePort = null!;
        private Button btnSaveLog = null!;
        private Button btnClearLog = null!;
        private StatusStrip statusStrip = null!;
        private ToolStripStatusLabel lblStatus = null!;
        private ContextMenuStrip logContextMenu = null!;

        private readonly Font _regularFont = new Font("Segoe UI", 9.5F, FontStyle.Regular);
        private readonly Font _boldFont = new Font("Segoe UI", 9.5F, FontStyle.Bold);
        private readonly Font _italicFont = new Font("Segoe UI", 9.5F, FontStyle.Italic);

        private CancellationTokenSource? _listenerCts;
        private TcpListener? _tcpListener;

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

        [StructLayout(LayoutKind.Sequential)]
        private struct FLASHWINFO
        {
            public uint cbSize;
            public IntPtr hwnd;
            public uint dwFlags;
            public uint uCount;
            public uint dwTimeout;
        }

        private const uint FLASHW_ALL = 3;
        private const uint FLASHW_TIMERNOFG = 12;

        [STAThread]
        public static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new ChatForm());
        }

        public ChatForm()
        {
            InitializeComponent();

            AppendSystemLog("Welcome to ChatSharp! End-to-end encrypted with ECDH & AES-256-GCM.");
            ShowMyAddr();
            txtMsg.Focus();

            if (int.TryParse(txtListeningPort.Text.Trim(), out int port))
            {
                _ = StartListeningAsync(port);
            }
        }

        private void InitializeComponent()
        {
            Text = "ChatSharp - Secure P2P Chat & File Transfer (ECDH + AES-256-GCM)";
            Size = new Size(650, 660);
            MinimumSize = new Size(540, 480);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9F);

            // Context menu for RichTextBox log
            logContextMenu = new ContextMenuStrip();
            var copyItem = new ToolStripMenuItem("Copy", null, (s, e) =>
            {
                if (!string.IsNullOrEmpty(rtbLog.SelectedText))
                    Clipboard.SetText(rtbLog.SelectedText);
            })
            { ShortcutKeys = Keys.Control | Keys.C };

            var selectAllItem = new ToolStripMenuItem("Select All", null, (s, e) => rtbLog.SelectAll())
            { ShortcutKeys = Keys.Control | Keys.A };

            var saveItem = new ToolStripMenuItem("Save Chat History...", null, (s, e) => SaveChatHistory())
            { ShortcutKeys = Keys.Control | Keys.S };

            var clearItem = new ToolStripMenuItem("Clear Chat History", null, (s, e) => ClearChatHistory());

            logContextMenu.Items.AddRange(new ToolStripItem[]
            {
                copyItem,
                selectAllItem,
                new ToolStripSeparator(),
                saveItem,
                clearItem
            });

            // Rich log display (center)
            rtbLog = new RichTextBox
            {
                ReadOnly = true,
                ScrollBars = RichTextBoxScrollBars.Vertical,
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(248, 249, 250),
                BorderStyle = BorderStyle.None,
                Font = _regularFont,
                ContextMenuStrip = logContextMenu
            };

            // Top settings bar
            FlowLayoutPanel panelTopSettings = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 38,
                Padding = new Padding(5, 5, 5, 0),
                FlowDirection = FlowDirection.LeftToRight
            };

            Label lblListening = new Label { Text = "Port:", AutoSize = true, Margin = new Padding(3, 6, 2, 0) };
            txtListeningPort = new TextBox { Text = "3333", Width = 48 };
            btnChangePort = new Button { Text = "Change", AutoSize = true, Cursor = Cursors.Hand };
            btnChangePort.Click += async (s, e) =>
            {
                if (int.TryParse(txtListeningPort.Text.Trim(), out int newPort) && newPort > 0 && newPort <= 65535)
                {
                    await StartListeningAsync(newPort);
                }
                else
                {
                    AppendErrorLog("Please enter a valid listening port (1-65535).");
                }
            };

            Label lblNick = new Label { Text = "Nickname:", AutoSize = true, Margin = new Padding(10, 6, 2, 0) };
            txtNickname = new TextBox { Text = Dns.GetHostName(), Width = 100 };

            btnSaveLog = new Button { Text = "Save Log", AutoSize = true, Cursor = Cursors.Hand, Margin = new Padding(15, 2, 3, 0) };
            btnSaveLog.Click += (s, e) => SaveChatHistory();

            btnClearLog = new Button { Text = "Clear", AutoSize = true, Cursor = Cursors.Hand, Margin = new Padding(3, 2, 3, 0) };
            btnClearLog.Click += (s, e) => ClearChatHistory();

            panelTopSettings.Controls.AddRange(new Control[]
            {
                lblListening, txtListeningPort, btnChangePort,
                lblNick, txtNickname,
                btnSaveLog, btnClearLog
            });

            // Bottom container
            Panel panelBottom = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 145,
                Padding = new Padding(6)
            };

            // Destination bar (Recent IP dropdown, Port, Send File)
            FlowLayoutPanel panelDestSettings = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 36,
                FlowDirection = FlowDirection.LeftToRight
            };

            Label lblDest = new Label { Text = "Destination IP:", AutoSize = true, Margin = new Padding(3, 6, 3, 0) };
            cbDestIp = new ComboBox
            {
                Width = 135,
                DropDownStyle = ComboBoxStyle.DropDown
            };
            cbDestIp.Items.AddRange(new object[] { "127.0.0.1", "localhost" });
            cbDestIp.SelectedIndex = 0;

            Label lblPort = new Label { Text = "Port:", AutoSize = true, Margin = new Padding(10, 6, 3, 0) };
            txtDestPort = new TextBox { Text = "3333", Width = 50 };

            btnSendFile = new Button
            {
                Text = "📎 Send File...",
                AutoSize = true,
                Cursor = Cursors.Hand,
                Margin = new Padding(15, 2, 3, 0)
            };
            btnSendFile.Click += BtnSendFile_Click;

            panelDestSettings.Controls.AddRange(new Control[] { lblDest, cbDestIp, lblPort, txtDestPort, btnSendFile });

            // Middle input message & send button
            Panel panelMiddle = new Panel
            {
                Dock = DockStyle.Fill
            };

            btnSend = new Button
            {
                Text = "Send",
                Dock = DockStyle.Right,
                Width = 85,
                Cursor = Cursors.Hand,
                Font = new Font("Segoe UI", 9.5F, FontStyle.Bold)
            };
            btnSend.Click += BtnSend_Click;

            txtMsg = new TextBox
            {
                Multiline = true,
                Dock = DockStyle.Fill,
                ScrollBars = ScrollBars.Vertical,
                Font = new Font("Segoe UI", 9.5F)
            };

            // Send on Enter, Shift+Enter for newline
            txtMsg.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter && !e.Shift)
                {
                    e.SuppressKeyPress = true;
                    btnSend.PerformClick();
                }
            };

            panelMiddle.Controls.Add(txtMsg);
            panelMiddle.Controls.Add(btnSend);

            panelBottom.Controls.Add(panelMiddle);
            panelBottom.Controls.Add(panelDestSettings);

            // Status bar
            statusStrip = new StatusStrip();
            lblStatus = new ToolStripStatusLabel
            {
                Text = "Ready",
                Spring = true,
                TextAlign = ContentAlignment.MiddleLeft
            };
            statusStrip.Items.Add(lblStatus);

            Controls.Add(rtbLog);
            Controls.Add(panelTopSettings);
            Controls.Add(panelBottom);
            Controls.Add(statusStrip);
        }

        private void SetStatus(string status)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action<string>(SetStatus), status);
                return;
            }
            lblStatus.Text = status;
        }

        private void AppendColoredChunk(string text, Color color, Font font)
        {
            rtbLog.SelectionStart = rtbLog.TextLength;
            rtbLog.SelectionLength = 0;
            rtbLog.SelectionColor = color;
            rtbLog.SelectionFont = font;
            rtbLog.AppendText(text);
        }

        private void AppendSentLog(string dest, string nickname, string message)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action<string, string, string>(AppendSentLog), dest, nickname, message);
                return;
            }

            string time = DateTime.Now.ToString("HH:mm:ss");
            AppendColoredChunk($"[{time}] ", Color.Gray, _regularFont);
            AppendColoredChunk($"[YOU -> {dest}] ", Color.FromArgb(0, 102, 204), _boldFont);
            AppendColoredChunk($"{nickname}: ", Color.FromArgb(30, 30, 30), _boldFont);
            AppendColoredChunk($"{message}\r\n", Color.FromArgb(20, 20, 20), _regularFont);

            ScrollLogToBottom();
        }

        private void AppendSentFileLog(string dest, string fileName, long fileSize)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action<string, string, long>(AppendSentFileLog), dest, fileName, fileSize);
                return;
            }

            string time = DateTime.Now.ToString("HH:mm:ss");
            AppendColoredChunk($"[{time}] ", Color.Gray, _regularFont);
            AppendColoredChunk($"[YOU -> {dest}] ", Color.FromArgb(0, 102, 204), _boldFont);
            AppendColoredChunk("📎 Sent file: ", Color.FromArgb(111, 66, 193), _boldFont);
            AppendColoredChunk($"{fileName} ({FormatFileSize(fileSize)})\r\n", Color.FromArgb(30, 30, 30), _regularFont);

            ScrollLogToBottom();
        }

        private void AppendReceivedLog(string senderAddr, string fullMessage)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action<string, string>(AppendReceivedLog), senderAddr, fullMessage);
                return;
            }

            string time = DateTime.Now.ToString("HH:mm:ss");
            AppendColoredChunk($"[{time}] ", Color.Gray, _regularFont);
            AppendColoredChunk($"[RECV from {senderAddr}] ", Color.FromArgb(25, 135, 84), _boldFont);

            int colonIndex = fullMessage.IndexOf(':');
            if (colonIndex > 0)
            {
                string senderNick = fullMessage.Substring(0, colonIndex).Trim();
                string body = fullMessage.Substring(colonIndex + 1).TrimStart();
                AppendColoredChunk($"{senderNick}: ", Color.FromArgb(20, 100, 20), _boldFont);
                AppendColoredChunk($"{body}\r\n", Color.Black, _regularFont);
            }
            else
            {
                AppendColoredChunk($"{fullMessage}\r\n", Color.Black, _regularFont);
            }

            ScrollLogToBottom();
        }

        private void AppendReceivedFileLog(string senderAddr, string senderNick, string fileName, long fileSize)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action<string, string, string, long>(AppendReceivedFileLog), senderAddr, senderNick, fileName, fileSize);
                return;
            }

            string time = DateTime.Now.ToString("HH:mm:ss");
            AppendColoredChunk($"[{time}] ", Color.Gray, _regularFont);
            AppendColoredChunk($"[RECV from {senderAddr}] ", Color.FromArgb(25, 135, 84), _boldFont);
            AppendColoredChunk($"📎 {senderNick} sent file: ", Color.FromArgb(111, 66, 193), _boldFont);
            AppendColoredChunk($"{fileName} ({FormatFileSize(fileSize)})\r\n", Color.FromArgb(30, 30, 30), _regularFont);

            ScrollLogToBottom();
        }

        private void AppendSystemLog(string message)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action<string>(AppendSystemLog), message);
                return;
            }

            string time = DateTime.Now.ToString("HH:mm:ss");
            AppendColoredChunk($"[{time}] ", Color.Gray, _regularFont);
            AppendColoredChunk("[SYSTEM] ", Color.FromArgb(108, 117, 125), _boldFont);
            AppendColoredChunk($"{message}\r\n", Color.FromArgb(73, 80, 87), _italicFont);

            ScrollLogToBottom();
        }

        private void AppendErrorLog(string message)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action<string>(AppendErrorLog), message);
                return;
            }

            string time = DateTime.Now.ToString("HH:mm:ss");
            AppendColoredChunk($"[{time}] ", Color.Gray, _regularFont);
            AppendColoredChunk("[ERROR] ", Color.FromArgb(220, 53, 69), _boldFont);
            AppendColoredChunk($"{message}\r\n", Color.FromArgb(180, 40, 40), _boldFont);

            ScrollLogToBottom();
        }

        private void ScrollLogToBottom()
        {
            rtbLog.SelectionStart = rtbLog.TextLength;
            rtbLog.SelectionLength = 0;
            rtbLog.ScrollToCaret();
        }

        public static string FormatFileSize(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            string[] suffixes = { "KB", "MB", "GB" };
            int i = -1;
            double d = bytes;
            while (d >= 1024 && i < suffixes.Length - 1)
            {
                d /= 1024;
                i++;
            }
            return $"{d:0.0} {suffixes[i]}";
        }

        private void SaveChatHistory()
        {
            using var sfd = new SaveFileDialog
            {
                Title = "Save Chat History",
                Filter = "Text Files (*.txt)|*.txt|All Files (*.*)|*.*",
                FileName = $"ChatSharp_History_{DateTime.Now:yyyyMMdd_HHmmss}.txt"
            };

            if (sfd.ShowDialog(this) == DialogResult.OK)
            {
                try
                {
                    File.WriteAllText(sfd.FileName, rtbLog.Text, Encoding.UTF8);
                    SetStatus($"Saved chat to {Path.GetFileName(sfd.FileName)}");
                    AppendSystemLog($"Chat history saved to {sfd.FileName}");
                }
                catch (Exception ex)
                {
                    AppendErrorLog($"Failed to save chat history: {ex.Message}");
                }
            }
        }

        private void ClearChatHistory()
        {
            rtbLog.Clear();
            AppendSystemLog("Chat history cleared.");
        }

        private void AddRecentDestination(string dest)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action<string>(AddRecentDestination), dest);
                return;
            }

            if (!cbDestIp.Items.Contains(dest))
            {
                cbDestIp.Items.Insert(0, dest);
                while (cbDestIp.Items.Count > 10)
                {
                    cbDestIp.Items.RemoveAt(cbDestIp.Items.Count - 1);
                }
                cbDestIp.SelectedItem = dest;
            }
        }

        private void ShowMyAddr()
        {
            string? myAddr = null;
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                        ni.OperationalStatus != OperationalStatus.Up)
                        continue;

                    foreach (UnicastIPAddressInformation ip in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ip.Address.AddressFamily == AddressFamily.InterNetwork)
                        {
                            myAddr = ip.Address.ToString();
                            break;
                        }
                    }
                    if (myAddr != null) break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }

            AppendSystemLog($"Local IPv4 address: {myAddr ?? "127.0.0.1"}");
        }

        private async Task StartListeningAsync(int port)
        {
            StopListening();

            _listenerCts = new CancellationTokenSource();
            CancellationToken token = _listenerCts.Token;

            try
            {
                _tcpListener = new TcpListener(IPAddress.Any, port);
                _tcpListener.Start();
                SetStatus($"Listening on port {port}");
                AppendSystemLog($"Listening for connections on port {port} (ECDH + AES-256-GCM)...");
            }
            catch (Exception ex)
            {
                SetStatus($"Port {port} unavailable");
                AppendErrorLog($"Could not listen on port {port}: {ex.Message}");
                return;
            }

            try
            {
                while (!token.IsCancellationRequested)
                {
                    TcpClient client = await _tcpListener.AcceptTcpClientAsync(token);
                    _ = HandleIncomingClientAsync(client, token);
                }
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown when stopped
            }
            catch (SocketException)
            {
                // Normal when listener is closed
            }
            catch (Exception ex)
            {
                AppendErrorLog($"Listener error: {ex.Message}");
            }
        }

        private void StopListening()
        {
            if (_listenerCts != null)
            {
                _listenerCts.Cancel();
                _listenerCts.Dispose();
                _listenerCts = null;
            }

            if (_tcpListener != null)
            {
                try { _tcpListener.Stop(); } catch { }
                _tcpListener = null;
            }
        }

        private async void BtnSend_Click(object? sender, EventArgs e)
        {
            string dest = cbDestIp.Text.Trim();
            if (string.IsNullOrEmpty(dest))
            {
                AppendErrorLog("Destination IP cannot be empty.");
                return;
            }

            if (!int.TryParse(txtDestPort.Text.Trim(), out int port) || port <= 0 || port > 65535)
            {
                AppendErrorLog("Invalid destination port. Must be between 1 and 65535.");
                return;
            }

            string textToSend = txtMsg.Text.Trim();
            if (string.IsNullOrEmpty(textToSend)) return;

            btnSend.Enabled = false;
            btnSendFile.Enabled = false;
            txtMsg.Enabled = false;
            SetStatus($"Connecting to {dest}:{port}...");

            try
            {
                using var client = new TcpClient();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

                await client.ConnectAsync(dest, port, cts.Token);
                SetStatus($"Exchanging ECDH keys with {dest}:{port}...");

                using var aliceEcdh = CryptoHelper.CreateEcdh();
                using NetworkStream stream = client.GetStream();
                using StreamReader reader = new StreamReader(stream, Encoding.UTF8);
                using StreamWriter writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };

                // 1. Send Alice's ECDH public key
                string alicePublicKey = CryptoHelper.ExportPublicKey(aliceEcdh);
                await writer.WriteLineAsync(alicePublicKey.AsMemory(), cts.Token);

                // 2. Receive Bob's ECDH public key
                string? bobPublicKey = await reader.ReadLineAsync(cts.Token);
                if (string.IsNullOrEmpty(bobPublicKey))
                {
                    AppendErrorLog($"{dest}: Peer did not return an ECDH public key.");
                    return;
                }

                // 3. Derive 256-bit shared key via ECDH
                byte[]? sharedKey = CryptoHelper.TryDeriveSharedSecret(aliceEcdh, bobPublicKey);
                if (sharedKey == null)
                {
                    AppendErrorLog($"{dest}: Failed to derive ECDH shared secret.");
                    return;
                }

                // 4. Encrypt ChatPacket with AES-256-GCM
                string senderNick = txtNickname.Text.Trim();
                var packet = new ChatPacket
                {
                    Type = "chat",
                    Sender = senderNick,
                    Content = textToSend
                };

                string jsonPayload = JsonSerializer.Serialize(packet);
                byte[] packedPayload = CryptoHelper.EncryptAesGcm(jsonPayload, sharedKey);
                string payloadBase64 = Convert.ToBase64String(packedPayload);

                await writer.WriteLineAsync(payloadBase64.AsMemory(), cts.Token);

                AppendSentLog(dest, senderNick, textToSend);
                AddRecentDestination(dest);
                txtMsg.Clear();
                SetStatus($"Ready | Listening on port {txtListeningPort.Text}");
            }
            catch (OperationCanceledException)
            {
                AppendErrorLog($"{dest}: Connection timed out.");
                SetStatus("Connection timed out");
            }
            catch (SocketException ex)
            {
                AppendErrorLog($"{dest}: Connection error ({ex.SocketErrorCode}).");
                SetStatus("Connection failed");
            }
            catch (Exception ex)
            {
                AppendErrorLog($"{dest}: {ex.Message}");
                SetStatus("Error");
            }
            finally
            {
                btnSend.Enabled = true;
                btnSendFile.Enabled = true;
                txtMsg.Enabled = true;
                txtMsg.Focus();
            }
        }

        private async void BtnSendFile_Click(object? sender, EventArgs e)
        {
            string dest = cbDestIp.Text.Trim();
            if (string.IsNullOrEmpty(dest))
            {
                AppendErrorLog("Destination IP cannot be empty.");
                return;
            }

            if (!int.TryParse(txtDestPort.Text.Trim(), out int port) || port <= 0 || port > 65535)
            {
                AppendErrorLog("Invalid destination port. Must be between 1 and 65535.");
                return;
            }

            using var ofd = new OpenFileDialog
            {
                Title = "Select File to Send",
                Filter = "All Files (*.*)|*.*",
                Multiselect = false
            };

            if (ofd.ShowDialog(this) != DialogResult.OK) return;

            var fileInfo = new FileInfo(ofd.FileName);
            const long maxFileSize = 1024L * 1024 * 1024; // 1 GB limit
            if (fileInfo.Length > maxFileSize)
            {
                AppendErrorLog($"File size ({FormatFileSize(fileInfo.Length)}) exceeds the 1 GB limit.");
                return;
            }

            if (fileInfo.Length == 0)
            {
                AppendErrorLog("Cannot send an empty file (0 bytes).");
                return;
            }

            btnSend.Enabled = false;
            btnSendFile.Enabled = false;
            txtMsg.Enabled = false;
            SetStatus($"Preparing {fileInfo.Name} ({FormatFileSize(fileInfo.Length)})...");

            try
            {
                const int chunkSize = 1024 * 1024; // 1 MB streaming chunks for low memory overhead
                int totalChunks = (int)Math.Ceiling((double)fileInfo.Length / chunkSize);

                using var client = new TcpClient();
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(30));

                SetStatus($"Connecting to {dest}:{port}...");
                await client.ConnectAsync(dest, port, cts.Token);

                SetStatus($"Exchanging ECDH keys with {dest}:{port}...");
                using var aliceEcdh = CryptoHelper.CreateEcdh();
                using NetworkStream stream = client.GetStream();
                using StreamReader reader = new StreamReader(stream, Encoding.UTF8);
                using StreamWriter writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };

                // 1. Send Alice's ECDH public key
                string alicePublicKey = CryptoHelper.ExportPublicKey(aliceEcdh);
                await writer.WriteLineAsync(alicePublicKey.AsMemory(), cts.Token);

                // 2. Receive Bob's ECDH public key
                string? bobPublicKey = await reader.ReadLineAsync(cts.Token);
                if (string.IsNullOrEmpty(bobPublicKey))
                {
                    AppendErrorLog($"{dest}: Peer did not return an ECDH public key.");
                    return;
                }

                // 3. Derive 256-bit shared key via ECDH
                byte[]? sharedKey = CryptoHelper.TryDeriveSharedSecret(aliceEcdh, bobPublicKey);
                if (sharedKey == null)
                {
                    AppendErrorLog($"{dest}: Failed to derive ECDH shared secret.");
                    return;
                }

                // 4. Send file_meta packet
                var metaPacket = new ChatPacket
                {
                    Type = "file_meta",
                    Sender = txtNickname.Text.Trim(),
                    FileName = fileInfo.Name,
                    FileSize = fileInfo.Length,
                    TotalChunks = totalChunks
                };

                string metaJson = JsonSerializer.Serialize(metaPacket);
                byte[] packedMeta = CryptoHelper.EncryptAesGcm(metaJson, sharedKey);
                await writer.WriteLineAsync(Convert.ToBase64String(packedMeta).AsMemory(), cts.Token);

                // 5. Stream chunks with constant low memory usage
                await using (var fs = new FileStream(ofd.FileName, FileMode.Open, FileAccess.Read, FileShare.Read, chunkSize, useAsync: true))
                {
                    byte[] buffer = new byte[chunkSize];
                    long bytesSent = 0;

                    for (int chunkIndex = 0; chunkIndex < totalChunks; chunkIndex++)
                    {
                        int bytesRead = await fs.ReadAsync(buffer.AsMemory(0, chunkSize), cts.Token);
                        if (bytesRead == 0) break;

                        byte[] chunkBytes = bytesRead == chunkSize ? buffer : buffer[..bytesRead];

                        var chunkPacket = new ChatPacket
                        {
                            Type = "file_chunk",
                            ChunkIndex = chunkIndex,
                            TotalChunks = totalChunks,
                            FileData = Convert.ToBase64String(chunkBytes)
                        };

                        string chunkJson = JsonSerializer.Serialize(chunkPacket);
                        byte[] packedChunk = CryptoHelper.EncryptAesGcm(chunkJson, sharedKey);
                        await writer.WriteLineAsync(Convert.ToBase64String(packedChunk).AsMemory(), cts.Token);

                        bytesSent += bytesRead;
                        int progress = (int)((bytesSent * 100) / fileInfo.Length);
                        SetStatus($"Sending {fileInfo.Name}: {progress}% ({FormatFileSize(bytesSent)} / {FormatFileSize(fileInfo.Length)})...");
                    }
                }

                AppendSentFileLog(dest, fileInfo.Name, fileInfo.Length);
                AddRecentDestination(dest);
                SetStatus($"Ready | File {fileInfo.Name} sent successfully");
            }
            catch (OperationCanceledException)
            {
                AppendErrorLog($"{dest}: File transfer timed out.");
                SetStatus("Transfer timed out");
            }
            catch (SocketException ex)
            {
                AppendErrorLog($"{dest}: Connection error ({ex.SocketErrorCode}).");
                SetStatus("Connection failed");
            }
            catch (Exception ex)
            {
                AppendErrorLog($"{dest}: {ex.Message}");
                SetStatus("Error");
            }
            finally
            {
                btnSend.Enabled = true;
                btnSendFile.Enabled = true;
                txtMsg.Enabled = true;
                txtMsg.Focus();
            }
        }

        private async Task HandleIncomingClientAsync(TcpClient clientSocket, CancellationToken serverToken)
        {
            using (clientSocket)
            using (var bobEcdh = CryptoHelper.CreateEcdh())
            using (NetworkStream stream = clientSocket.GetStream())
            using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
            using (StreamWriter writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true })
            {
                try
                {
                    using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(serverToken, timeoutCts.Token);
                    CancellationToken token = linkedCts.Token;

                    // 1. Receive sender's ECDH public key
                    string? senderPublicKey = await reader.ReadLineAsync(token);
                    if (string.IsNullOrEmpty(senderPublicKey)) return;

                    // 2. Send receiver's ECDH public key back
                    string receiverPublicKey = CryptoHelper.ExportPublicKey(bobEcdh);
                    await writer.WriteLineAsync(receiverPublicKey.AsMemory(), token);

                    // 3. Derive 256-bit shared key via ECDH
                    byte[]? sharedKey = CryptoHelper.TryDeriveSharedSecret(bobEcdh, senderPublicKey);
                    if (sharedKey == null) return;

                    // 4. Receive and decrypt first payload
                    string? payloadBase64 = await reader.ReadLineAsync(token);
                    if (string.IsNullOrEmpty(payloadBase64)) return;

                    byte[] packedPayload = Convert.FromBase64String(payloadBase64);
                    string decryptedJson = CryptoHelper.DecryptAesGcm(packedPayload, sharedKey);

                    string clientAddr = (clientSocket.Client.RemoteEndPoint as IPEndPoint)?.ToString() ?? "unknown";

                    // Handle structured ChatPacket or fallback
                    if (decryptedJson.StartsWith("{") && decryptedJson.EndsWith("}"))
                    {
                        ChatPacket? packet = null;
                        try
                        {
                            packet = JsonSerializer.Deserialize<ChatPacket>(decryptedJson);
                        }
                        catch
                        {
                            // Fallback
                        }

                        if (packet != null)
                        {
                            if (packet.Type == "file_meta")
                            {
                                // Chunked file transfer (supports up to 1 GB)
                                await HandleChunkedFileReceiveAsync(reader, sharedKey, packet, clientAddr, token);
                                return;
                            }
                            else if (packet.Type == "file")
                            {
                                // Single-packet file (legacy or small files)
                                AppendReceivedFileLog(clientAddr, packet.Sender, packet.FileName ?? "unnamed_file", packet.FileSize);
                                NotifyNewMessage();

                                if (!string.IsNullOrEmpty(packet.FileData))
                                {
                                    byte[] fileBytes = Convert.FromBase64String(packet.FileData);
                                    BeginInvoke(new Action(async () =>
                                    {
                                        await PromptSaveFileAsync(packet.Sender, packet.FileName ?? "downloaded_file", fileBytes);
                                    }));
                                }
                                return;
                            }
                            else if (packet.Type == "chat")
                            {
                                AppendReceivedLog(clientAddr, $"{packet.Sender}: {packet.Content}");
                                NotifyNewMessage();
                                return;
                            }
                        }
                    }

                    // Fallback for legacy plain text messages
                    AppendReceivedLog(clientAddr, decryptedJson);
                    NotifyNewMessage();
                }
                catch (OperationCanceledException)
                {
                    // Timeout or cancellation
                }
                catch (CryptographicException)
                {
                    AppendErrorLog("Security alert: Received message failed integrity verification (tampered or corrupted).");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Client Handler Error] {ex.Message}");
                }
            }
        }

        private async Task HandleChunkedFileReceiveAsync(StreamReader reader, byte[] sharedKey, ChatPacket metaPacket, string clientAddr, CancellationToken token)
        {
            string fileName = metaPacket.FileName ?? "downloaded_file";
            string sender = metaPacket.Sender;
            long totalSize = metaPacket.FileSize;
            int totalChunks = metaPacket.TotalChunks;

            string tempFilePath = Path.Combine(Path.GetTempPath(), $"ChatSharp_{Guid.NewGuid():N}.tmp");

            try
            {
                SetStatus($"Receiving {fileName} ({FormatFileSize(totalSize)}) from {sender}...");

                await using (var fs = new FileStream(tempFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, useAsync: true))
                {
                    long totalBytesReceived = 0;

                    for (int i = 0; i < totalChunks; i++)
                    {
                        string? chunkLine = await reader.ReadLineAsync(token);
                        if (string.IsNullOrEmpty(chunkLine))
                        {
                            throw new IOException("Connection closed prematurely during file transfer.");
                        }

                        byte[] packedChunk = Convert.FromBase64String(chunkLine);
                        string chunkJson = CryptoHelper.DecryptAesGcm(packedChunk, sharedKey);
                        var chunkPacket = JsonSerializer.Deserialize<ChatPacket>(chunkJson);

                        if (chunkPacket?.FileData == null)
                        {
                            throw new InvalidDataException("Invalid chunk payload.");
                        }

                        byte[] chunkBytes = Convert.FromBase64String(chunkPacket.FileData);
                        await fs.WriteAsync(chunkBytes.AsMemory(), token);

                        totalBytesReceived += chunkBytes.Length;
                        int progress = totalSize > 0 ? (int)((totalBytesReceived * 100) / totalSize) : 100;
                        SetStatus($"Receiving {fileName}: {progress}% ({FormatFileSize(totalBytesReceived)} / {FormatFileSize(totalSize)})...");
                    }
                }

                AppendReceivedFileLog(clientAddr, sender, fileName, totalSize);
                NotifyNewMessage();
                SetStatus($"Received {fileName} ({FormatFileSize(totalSize)})");

                // Prompt user on UI thread to save the received file
                BeginInvoke(new Action(async () =>
                {
                    await PromptSaveReceivedTempFileAsync(sender, fileName, tempFilePath, totalSize);
                }));
            }
            catch (Exception ex)
            {
                try { if (File.Exists(tempFilePath)) File.Delete(tempFilePath); } catch { }
                AppendErrorLog($"Error receiving file '{fileName}': {ex.Message}");
                SetStatus("File receive failed");
            }
        }

        private async Task PromptSaveReceivedTempFileAsync(string sender, string fileName, string tempFilePath, long fileSize)
        {
            string formattedSize = FormatFileSize(fileSize);
            DialogResult result = MessageBox.Show(
                this,
                $"Received file '{fileName}' ({formattedSize}) from {sender}.\r\n\r\nWould you like to save it to your computer?",
                "Incoming File Transfer (Verified AES-256-GCM)",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (result == DialogResult.Yes)
            {
                using var sfd = new SaveFileDialog
                {
                    Title = "Save Received File",
                    FileName = fileName,
                    Filter = "All Files (*.*)|*.*"
                };

                if (sfd.ShowDialog(this) == DialogResult.OK)
                {
                    try
                    {
                        if (File.Exists(sfd.FileName)) File.Delete(sfd.FileName);
                        File.Move(tempFilePath, sfd.FileName);
                        AppendSystemLog($"Saved '{fileName}' to {sfd.FileName}");
                        SetStatus($"File saved: {Path.GetFileName(sfd.FileName)}");
                        return;
                    }
                    catch (Exception)
                    {
                        // Fallback to streaming copy if cross-volume move is required
                        try
                        {
                            await using (var src = new FileStream(tempFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, useAsync: true))
                            await using (var dst = new FileStream(sfd.FileName, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, useAsync: true))
                            {
                                await src.CopyToAsync(dst);
                            }
                            AppendSystemLog($"Saved '{fileName}' to {sfd.FileName}");
                            SetStatus($"File saved: {Path.GetFileName(sfd.FileName)}");
                        }
                        catch (Exception ex)
                        {
                            AppendErrorLog($"Failed to save file: {ex.Message}");
                        }
                    }
                }
                else
                {
                    AppendSystemLog($"Save canceled for '{fileName}'.");
                }
            }
            else
            {
                AppendSystemLog($"Declined file '{fileName}' from {sender}.");
            }

            try { if (File.Exists(tempFilePath)) File.Delete(tempFilePath); } catch { }
        }

        private async Task PromptSaveFileAsync(string sender, string fileName, byte[] fileBytes)
        {
            string formattedSize = FormatFileSize(fileBytes.Length);
            DialogResult result = MessageBox.Show(
                this,
                $"Received file '{fileName}' ({formattedSize}) from {sender}.\r\n\r\nWould you like to save it to your computer?",
                "Incoming File Transfer (Verified AES-256-GCM)",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (result == DialogResult.Yes)
            {
                using var sfd = new SaveFileDialog
                {
                    Title = "Save Received File",
                    FileName = fileName,
                    Filter = "All Files (*.*)|*.*"
                };

                if (sfd.ShowDialog(this) == DialogResult.OK)
                {
                    try
                    {
                        await File.WriteAllBytesAsync(sfd.FileName, fileBytes);
                        AppendSystemLog($"Saved '{fileName}' to {sfd.FileName}");
                        SetStatus($"File saved: {Path.GetFileName(sfd.FileName)}");
                    }
                    catch (Exception ex)
                    {
                        AppendErrorLog($"Failed to save file: {ex.Message}");
                    }
                }
                else
                {
                    AppendSystemLog($"Save canceled for '{fileName}'.");
                }
            }
            else
            {
                AppendSystemLog($"Declined file '{fileName}' from {sender}.");
            }
        }

        private void NotifyNewMessage()
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(NotifyNewMessage));
                return;
            }

            if (!Focused)
            {
                try
                {
                    System.Media.SystemSounds.Asterisk.Play();
                }
                catch { }

                FLASHWINFO fi = new FLASHWINFO
                {
                    cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
                    hwnd = Handle,
                    dwFlags = FLASHW_ALL | FLASHW_TIMERNOFG,
                    uCount = 3,
                    dwTimeout = 0
                };
                FlashWindowEx(ref fi);
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            StopListening();
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _regularFont.Dispose();
                _boldFont.Dispose();
                _italicFont.Dispose();
            }
            base.Dispose(disposing);
        }

        public class ChatPacket
        {
            public string Type { get; set; } = "chat";
            public string Sender { get; set; } = "";
            public string? Content { get; set; }
            public string? FileName { get; set; }
            public long FileSize { get; set; }
            public string? FileData { get; set; }
            public int ChunkIndex { get; set; }
            public int TotalChunks { get; set; }
        }

        public static class CryptoHelper
        {
            public static ECDiffieHellman CreateEcdh()
            {
                return ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            }

            public static string ExportPublicKey(ECDiffieHellman ecdh)
            {
                return Convert.ToBase64String(ecdh.ExportSubjectPublicKeyInfo());
            }

            public static byte[]? TryDeriveSharedSecret(ECDiffieHellman ecdh, string peerPublicKeyBase64)
            {
                try
                {
                    byte[] peerKeyBytes = Convert.FromBase64String(peerPublicKeyBase64);
                    using var peerEcdh = ECDiffieHellman.Create();
                    peerEcdh.ImportSubjectPublicKeyInfo(peerKeyBytes, out _);
                    return ecdh.DeriveKeyFromHash(peerEcdh.PublicKey, HashAlgorithmName.SHA256);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Crypto] Key derivation failed: {ex.Message}");
                    return null;
                }
            }

            public static byte[] EncryptAesGcm(string plaintext, byte[] key256)
            {
                byte[] plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
                byte[] nonce = new byte[AesGcm.NonceByteSizes.MaxSize]; // 12 bytes
                RandomNumberGenerator.Fill(nonce);

                byte[] tag = new byte[AesGcm.TagByteSizes.MaxSize]; // 16 bytes
                byte[] ciphertext = new byte[plaintextBytes.Length];

                using var aes = new AesGcm(key256, AesGcm.TagByteSizes.MaxSize);
                aes.Encrypt(nonce, plaintextBytes, ciphertext, tag);

                // Format: Nonce (12) + Tag (16) + Ciphertext
                byte[] packed = new byte[nonce.Length + tag.Length + ciphertext.Length];
                Buffer.BlockCopy(nonce, 0, packed, 0, nonce.Length);
                Buffer.BlockCopy(tag, 0, packed, nonce.Length, tag.Length);
                Buffer.BlockCopy(ciphertext, 0, packed, nonce.Length + tag.Length, ciphertext.Length);

                return packed;
            }

            public static string DecryptAesGcm(byte[] packedPayload, byte[] key256)
            {
                const int nonceSize = 12;
                const int tagSize = 16;
                if (packedPayload.Length < nonceSize + tagSize)
                {
                    throw new CryptographicException("Ciphertext payload is too short.");
                }

                byte[] nonce = packedPayload[..nonceSize];
                byte[] tag = packedPayload[nonceSize..(nonceSize + tagSize)];
                byte[] ciphertext = packedPayload[(nonceSize + tagSize)..];

                byte[] plaintextBytes = new byte[ciphertext.Length];
                using var aes = new AesGcm(key256, tagSize);
                aes.Decrypt(nonce, ciphertext, tag, plaintextBytes);

                return Encoding.UTF8.GetString(plaintextBytes);
            }
        }

        public static class ChatProtocol
        {
            public static char[] EncodeString(string input, int key)
            {
                char[] chars = input.ToCharArray();
                for (int i = 0; i < chars.Length; i++)
                {
                    chars[i] = (char)(chars[i] + key);
                }
                return chars;
            }

            public static char[] DecodeString(char[] input, int key)
            {
                char[] chars = new char[input.Length];
                for (int i = 0; i < input.Length; i++)
                {
                    chars[i] = (char)(chars[i] - key);
                }
                return chars;
            }
        }
    }
}