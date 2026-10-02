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
        private const byte PACKET_TYPE_CHAT = 1;
        private const byte PACKET_TYPE_FILE = 2;
        private const int CHUNK_SIZE = 1024 * 1024; // 1 MB binary chunks

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

            AppendSystemLog("Welcome to ChatSharp! End-to-end encrypted with ECDH & AES-256-GCM (high-speed binary framing).");
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
                AppendErrorLog($"Failed to retrieve IP address: {ex.Message}");
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
                AppendSystemLog($"Listening for connections on port {port} (ECDH + AES-256-GCM binary protocol)...");
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
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

                await client.ConnectAsync(dest, port, cts.Token);
                SetStatus($"Exchanging ECDH keys with {dest}:{port}...");

                using NetworkStream stream = client.GetStream();
                using var aliceEcdh = CryptoHelper.CreateEcdh();

                // 1. Exchange ECDH Public Keys in binary
                byte[] sharedKey = await PerformClientKeyExchangeAsync(stream, aliceEcdh, cts.Token);

                // 2. Send Chat Packet Type
                stream.WriteByte(PACKET_TYPE_CHAT);

                // 3. Encrypt & Send Chat Message
                string senderNick = txtNickname.Text.Trim();
                var packet = new ChatPacket
                {
                    Type = "chat",
                    Sender = senderNick,
                    Content = textToSend
                };

                byte[] jsonBytes = JsonSerializer.SerializeToUtf8Bytes(packet);
                await WriteEncryptedPacketAsync(stream, jsonBytes, sharedKey, cts.Token);

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
            SetStatus($"Connecting to {dest}:{port}...");

            try
            {
                int totalChunks = (int)Math.Ceiling((double)fileInfo.Length / CHUNK_SIZE);

                using var client = new TcpClient();
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(30));

                await client.ConnectAsync(dest, port, cts.Token);
                SetStatus($"Exchanging ECDH keys with {dest}:{port}...");

                using NetworkStream stream = client.GetStream();
                using var aliceEcdh = CryptoHelper.CreateEcdh();

                // 1. Exchange ECDH Public Keys in binary
                byte[] sharedKey = await PerformClientKeyExchangeAsync(stream, aliceEcdh, cts.Token);

                // 2. Send File Packet Type
                stream.WriteByte(PACKET_TYPE_FILE);

                // 3. Send File Offer Metadata (file name & size first)
                var offerPacket = new ChatPacket
                {
                    Type = "file_offer",
                    Sender = txtNickname.Text.Trim(),
                    FileName = fileInfo.Name,
                    FileSize = fileInfo.Length,
                    TotalChunks = totalChunks
                };

                byte[] metaBytes = JsonSerializer.SerializeToUtf8Bytes(offerPacket);
                await WriteEncryptedPacketAsync(stream, metaBytes, sharedKey, cts.Token);

                SetStatus($"Waiting for {dest} to accept '{fileInfo.Name}' ({FormatFileSize(fileInfo.Length)})...");
                AppendSystemLog($"Offered '{fileInfo.Name}' ({FormatFileSize(fileInfo.Length)}) to {dest}. Waiting for acceptance...");

                // 4. Wait for Receiver's Acceptance Response (1 = Accept, 0 = Decline)
                byte[] responseBytes = await ReadEncryptedPacketAsync(stream, sharedKey, cts.Token);
                if (responseBytes.Length == 0 || responseBytes[0] != 1)
                {
                    AppendSystemLog($"{dest} declined the file transfer for '{fileInfo.Name}'.");
                    SetStatus("File transfer declined by receiver");
                    return;
                }

                AppendSystemLog($"{dest} accepted '{fileInfo.Name}'. Starting transfer...");
                SetStatus($"Sending {fileInfo.Name}...");

                // 5. Start streaming chunks only after receiver accepts
                await using (var fs = new FileStream(ofd.FileName, FileMode.Open, FileAccess.Read, FileShare.Read, CHUNK_SIZE, useAsync: true))
                {
                    byte[] fileBuffer = new byte[CHUNK_SIZE];
                    long bytesSent = 0;

                    for (int chunkIndex = 0; chunkIndex < totalChunks; chunkIndex++)
                    {
                        int bytesRead = await fs.ReadAsync(fileBuffer.AsMemory(0, CHUNK_SIZE), cts.Token);
                        if (bytesRead == 0) break;

                        ReadOnlyMemory<byte> chunkSlice = fileBuffer.AsMemory(0, bytesRead);
                        await WriteEncryptedPacketAsync(stream, chunkSlice, sharedKey, cts.Token);

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
            using (NetworkStream stream = clientSocket.GetStream())
            using (var bobEcdh = CryptoHelper.CreateEcdh())
            {
                try
                {
                    using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(serverToken, timeoutCts.Token);
                    CancellationToken token = linkedCts.Token;

                    // 1. Binary ECDH Key Exchange
                    byte[] sharedKey = await PerformServerKeyExchangeAsync(stream, bobEcdh, token);

                    // 2. Read Packet Type
                    byte[] typeBuf = new byte[1];
                    await stream.ReadExactlyAsync(typeBuf, token);
                    byte packetType = typeBuf[0];

                    string clientAddr = (clientSocket.Client.RemoteEndPoint as IPEndPoint)?.ToString() ?? "unknown";

                    if (packetType == PACKET_TYPE_CHAT)
                    {
                        byte[] jsonBytes = await ReadEncryptedPacketAsync(stream, sharedKey, token);
                        var packet = JsonSerializer.Deserialize<ChatPacket>(jsonBytes);
                        if (packet != null)
                        {
                            AppendReceivedLog(clientAddr, $"{packet.Sender}: {packet.Content}");
                            NotifyNewMessage();
                        }
                    }
                    else if (packetType == PACKET_TYPE_FILE)
                    {
                        byte[] metaBytes = await ReadEncryptedPacketAsync(stream, sharedKey, token);
                        var metaPacket = JsonSerializer.Deserialize<ChatPacket>(metaBytes);
                        if (metaPacket != null)
                        {
                            await HandleBinaryFileReceiveAsync(stream, sharedKey, metaPacket, clientAddr, token);
                        }
                    }
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
                    AppendErrorLog($"Client connection error: {ex.Message}");
                }
            }
        }

        private async Task HandleBinaryFileReceiveAsync(Stream stream, byte[] sharedKey, ChatPacket metaPacket, string clientAddr, CancellationToken token)
        {
            string fileName = metaPacket.FileName ?? "downloaded_file";
            string sender = metaPacket.Sender;
            long totalSize = metaPacket.FileSize;
            int totalChunks = metaPacket.TotalChunks;

            NotifyNewMessage();
            SetStatus($"Incoming file request from {sender}: {fileName} ({FormatFileSize(totalSize)})");

            // Prompt user on UI thread BEFORE file transfer begins
            var tcs = new TaskCompletionSource<(bool Accepted, string? SavePath)>();

            BeginInvoke(new Action(() =>
            {
                string formattedSize = FormatFileSize(totalSize);
                DialogResult dr = MessageBox.Show(
                    this,
                    $"{sender} wants to send you:\r\n\r\n'{fileName}' ({formattedSize})\r\n\r\nDo you want to accept and download this file?",
                    "Incoming File Transfer Request",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);

                if (dr == DialogResult.Yes)
                {
                    using var sfd = new SaveFileDialog
                    {
                        Title = "Save Incoming File As...",
                        FileName = fileName,
                        Filter = "All Files (*.*)|*.*"
                    };

                    if (sfd.ShowDialog(this) == DialogResult.OK)
                    {
                        tcs.TrySetResult((true, sfd.FileName));
                        return;
                    }
                }

                tcs.TrySetResult((false, null));
            }));

            // Wait for user's decision (with cancellation support)
            (bool accepted, string? savePath) = await tcs.Task.WaitAsync(token);

            if (!accepted || string.IsNullOrEmpty(savePath))
            {
                // Send decline response byte (0)
                await WriteEncryptedPacketAsync(stream, new byte[] { 0 }, sharedKey, token);
                AppendSystemLog($"Declined file transfer for '{fileName}' from {sender}.");
                SetStatus($"Declined file '{fileName}'");
                return;
            }

            // Send accept response byte (1)
            await WriteEncryptedPacketAsync(stream, new byte[] { 1 }, sharedKey, token);
            AppendSystemLog($"Accepted '{fileName}' from {sender}. Downloading to {savePath}...");
            SetStatus($"Receiving {fileName} ({FormatFileSize(totalSize)})...");

            try
            {
                // Stream chunks directly into the user's chosen file path
                await using (var fs = new FileStream(savePath, FileMode.Create, FileAccess.Write, FileShare.None, CHUNK_SIZE, useAsync: true))
                {
                    long totalBytesReceived = 0;

                    for (int i = 0; i < totalChunks; i++)
                    {
                        byte[] chunkBytes = await ReadEncryptedPacketAsync(stream, sharedKey, token);
                        await fs.WriteAsync(chunkBytes, token);

                        totalBytesReceived += chunkBytes.Length;
                        int progress = totalSize > 0 ? (int)((totalBytesReceived * 100) / totalSize) : 100;
                        SetStatus($"Receiving {fileName}: {progress}% ({FormatFileSize(totalBytesReceived)} / {FormatFileSize(totalSize)})...");
                    }
                }

                AppendReceivedFileLog(clientAddr, sender, fileName, totalSize);
                AppendSystemLog($"File '{fileName}' saved successfully to {savePath}");
                NotifyNewMessage();
                SetStatus($"Received {fileName} ({FormatFileSize(totalSize)})");
            }
            catch (Exception ex)
            {
                try { if (File.Exists(savePath)) File.Delete(savePath); } catch { }
                AppendErrorLog($"Error receiving file '{fileName}': {ex.Message}");
                SetStatus("File receive failed");
            }
        }

        private static async Task<byte[]> PerformClientKeyExchangeAsync(Stream stream, ECDiffieHellman clientEcdh, CancellationToken token)
        {
            // Send client's 2-byte length + public key bytes
            byte[] clientPubKey = clientEcdh.ExportSubjectPublicKeyInfo();
            byte[] clientLenBytes = BitConverter.GetBytes((ushort)clientPubKey.Length);
            await stream.WriteAsync(clientLenBytes, token);
            await stream.WriteAsync(clientPubKey, token);

            // Read server's 2-byte length + public key bytes
            byte[] serverLenBuf = new byte[2];
            await stream.ReadExactlyAsync(serverLenBuf, token);
            ushort serverKeyLen = BitConverter.ToUInt16(serverLenBuf, 0);

            byte[] serverPubKey = new byte[serverKeyLen];
            await stream.ReadExactlyAsync(serverPubKey, token);

            using var serverEcdh = ECDiffieHellman.Create();
            serverEcdh.ImportSubjectPublicKeyInfo(serverPubKey, out _);
            return clientEcdh.DeriveKeyFromHash(serverEcdh.PublicKey, HashAlgorithmName.SHA256);
        }

        private static async Task<byte[]> PerformServerKeyExchangeAsync(Stream stream, ECDiffieHellman serverEcdh, CancellationToken token)
        {
            // Read client's 2-byte length + public key bytes
            byte[] clientLenBuf = new byte[2];
            await stream.ReadExactlyAsync(clientLenBuf, token);
            ushort clientKeyLen = BitConverter.ToUInt16(clientLenBuf, 0);

            byte[] clientPubKey = new byte[clientKeyLen];
            await stream.ReadExactlyAsync(clientPubKey, token);

            // Send server's 2-byte length + public key bytes
            byte[] serverPubKey = serverEcdh.ExportSubjectPublicKeyInfo();
            byte[] serverLenBytes = BitConverter.GetBytes((ushort)serverPubKey.Length);
            await stream.WriteAsync(serverLenBytes, token);
            await stream.WriteAsync(serverPubKey, token);

            using var clientEcdh = ECDiffieHellman.Create();
            clientEcdh.ImportSubjectPublicKeyInfo(clientPubKey, out _);
            return serverEcdh.DeriveKeyFromHash(clientEcdh.PublicKey, HashAlgorithmName.SHA256);
        }

        private static async Task WriteEncryptedPacketAsync(Stream stream, ReadOnlyMemory<byte> plaintext, byte[] key, CancellationToken token)
        {
            byte[] encrypted = CryptoHelper.EncryptAesGcm(plaintext.Span, key);
            byte[] lenBytes = BitConverter.GetBytes(encrypted.Length);
            await stream.WriteAsync(lenBytes, token);
            await stream.WriteAsync(encrypted, token);
        }

        private static async Task<byte[]> ReadEncryptedPacketAsync(Stream stream, byte[] key, CancellationToken token)
        {
            byte[] lenBuf = new byte[4];
            await stream.ReadExactlyAsync(lenBuf, token);
            int len = BitConverter.ToInt32(lenBuf, 0);

            if (len < 28 || len > 100 * 1024 * 1024)
            {
                throw new InvalidDataException($"Invalid packet length received: {len} bytes.");
            }

            byte[] payloadBuf = new byte[len];
            await stream.ReadExactlyAsync(payloadBuf, token);
            return CryptoHelper.DecryptAesGcm(payloadBuf, key);
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
            public int TotalChunks { get; set; }
        }

        public static class CryptoHelper
        {
            public static ECDiffieHellman CreateEcdh()
            {
                return ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            }

            public static byte[] EncryptAesGcm(ReadOnlySpan<byte> plaintext, byte[] key256)
            {
                byte[] nonce = new byte[AesGcm.NonceByteSizes.MaxSize]; // 12 bytes
                RandomNumberGenerator.Fill(nonce);

                byte[] tag = new byte[AesGcm.TagByteSizes.MaxSize]; // 16 bytes
                byte[] ciphertext = new byte[plaintext.Length];

                using var aes = new AesGcm(key256, AesGcm.TagByteSizes.MaxSize);
                aes.Encrypt(nonce, plaintext, ciphertext, tag);

                // Format: Nonce (12) + Tag (16) + Ciphertext
                byte[] packed = new byte[nonce.Length + tag.Length + ciphertext.Length];
                Buffer.BlockCopy(nonce, 0, packed, 0, nonce.Length);
                Buffer.BlockCopy(tag, 0, packed, nonce.Length, tag.Length);
                Buffer.BlockCopy(ciphertext, 0, packed, nonce.Length + tag.Length, ciphertext.Length);

                return packed;
            }

            public static byte[] DecryptAesGcm(ReadOnlySpan<byte> packedPayload, byte[] key256)
            {
                const int nonceSize = 12;
                const int tagSize = 16;
                if (packedPayload.Length < nonceSize + tagSize)
                {
                    throw new CryptographicException("Ciphertext payload is too short.");
                }

                ReadOnlySpan<byte> nonce = packedPayload.Slice(0, nonceSize);
                ReadOnlySpan<byte> tag = packedPayload.Slice(nonceSize, tagSize);
                ReadOnlySpan<byte> ciphertext = packedPayload.Slice(nonceSize + tagSize);

                byte[] plaintextBytes = new byte[ciphertext.Length];
                using var aes = new AesGcm(key256, tagSize);
                aes.Decrypt(nonce, ciphertext, tag, plaintextBytes);

                return plaintextBytes;
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