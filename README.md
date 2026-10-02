# ChatSharp 🔒💬

**ChatSharp** is a modern, lightweight peer-to-peer (P2P) desktop messaging and file transfer application built with C# and Windows Forms on .NET 10. It features end-to-end authenticated encryption using **ECDH (Elliptic-Curve Diffie-Hellman)** and **AES-256-GCM**.

---

## ✨ Features

- **🛡️ End-to-End Authenticated Encryption**:
  - **Key Exchange**: Ephemeral **ECDH** over the standard **NIST P-256** curve (`nistP256`) with SHA-256 key derivation (KDF). Neither private keys nor shared secrets are ever transmitted across the network, providing **Perfect Forward Secrecy (PFS)**.
  - **Authenticated Cipher**: **AES-256-GCM** (Galois/Counter Mode) with 12-byte random nonces per transmission and 16-byte authentication tags. Protects against eavesdropping and guarantees tamper-proof message integrity.
- **📎 High-Speed Binary File Transfer**:
  - Transfer files of any type (images, documents, archives, binaries) up to 1 GB at wire speed.
  - **Zero Base64 Overhead**: Directly streams raw binary AES-256-GCM chunks (`[4-byte Length] + [Nonce (12)] + [Tag (16)] + [Ciphertext]`), completely eliminating Base64 bandwidth bloat and memory allocations.
  - **Interactive Acceptance Handshake**: File metadata (name & size) is sent first. Transmission of file chunks begins only *after* the recipient accepts and selects a save location. If declined, zero data chunks are sent.
- **⚡ Fully Asynchronous & Non-Blocking**:
  - Built with modern `async`/`await` Task-based asynchronous networking (`TcpClient`, `TcpListener`).
  - The UI remains completely responsive with zero freezing during connection handshakes, timeouts, or file transfers.
- **🎨 Rich Chat UI**:
  - Color-coded message log (`RichTextBox`):
    - 🔵 **Sent Messages**: Highlighted in royal blue with bolded nickname.
    - 🟢 **Received Messages**: Highlighted in forest green with sender IP and bolded nickname.
    - 🟣 **File Transfers**: Styled in purple with file icon and formatted size.
    - ⚪ **System Logs**: Dimmed in slate gray with italicized text.
    - 🔴 **Security & Error Alerts**: Highlighted in bold crimson red.
  - Timestamps on every event (`[HH:mm:ss]`).
  - Auto-scrolling to the latest incoming message.
- **⌨️ Intuitive UX**:
  - Press <kbd>Enter</kbd> to send, or <kbd>Shift</kbd> + <kbd>Enter</kbd> to insert a newline.
  - **Recent Destinations Dropdown**: Automatically remembers and prioritizes recently messaged IP addresses.
  - **Context Menu & History**: Right-click to Copy, Select All, Clear, or Export chat history to a `.txt` file.
  - **Top Actions**: Quick "Save Log" and "Clear" buttons.
  - **Background Alerts**: Native Windows taskbar flashing (`FlashWindowEx`) and audio notification chime (`SystemSounds.Asterisk`) when messages arrive while the window is unfocused.
- **✅ Strict Type & Null Safety**:
  - Fully annotated with C# Nullable Reference Types (`<Nullable>enable</Nullable>`).
  - Compiles cleanly with **0 warnings** and **0 errors** under `-warnaserror`.

---

## 🏗️ Architecture & Security Model

```
   Sender (Alice)                                    Receiver (Bob)
   ==============                                    ==============
[Generate Ephemeral ECDH]                         [Listening on Port]
          |                                                |
          |------- 1. Alice Public Key (Binary: 2B+N) ---->|
          |                                       [Generate Ephemeral ECDH]
          |<------ 2. Bob Public Key (Binary: 2B+N) -------|
          |                                                |
[Derive 256-bit Key via SHA256]                   [Derive 256-bit Key via SHA256]
          |                                                |
   --- FILE TRANSFER HANDSHAKE ---                         |
          |                                                |
          |------- 3. File Offer (Name, Size, Chunks) ---->|
          |           (Encrypted Metadata)                 |
          |                                       [Prompt User & Select Path]
          |<------ 4. Acceptance Response (1=Yes, 0=No) ---|
          |                                                |
   [If Accepted: Start Stream]                             |
          |                                                |
          |------- 5. Binary AES-GCM Chunks -------------->|
          |           (Written directly to chosen path)    |
          |                                                |
       [Close]                                       [Completed & Notify]
```

1. **Key Agreement**: Alice and Bob generate ephemeral NIST P-256 keypairs and exchange only their SubjectPublicKeyInfo (X.509 format).
2. **KDF**: Both parties locally compute $K = \text{SHA-256}(d \cdot Q_{\text{peer}})$.
3. **AEAD Encryption**: Plaintext (chat message or file payload) is serialized into a structured JSON envelope and encrypted with AES-256-GCM using $K$, a fresh 12-byte random nonce, and a 16-byte authentication tag.
4. **Verification**: If an attacker tampers with or corrupts a single byte in transit, AES-GCM verification fails immediately, discarding the packet and triggering a security warning.

---

## 📋 Prerequisites

- **OS**: Windows 10 / 11 (or Windows Server 2016+)
- **SDK / Runtime**: [.NET 10.0 SDK](https://dotnet.microsoft.com/download) (or later) with Windows Desktop Runtime.

---

## 🚀 Getting Started

### 1. Build the Project

Clone or open the directory in PowerShell / Command Prompt and run:

```powershell
dotnet build -c Release
```

To verify with zero warnings as errors:

```powershell
dotnet build --no-incremental -warnaserror
```

### 2. Run the Application

Launch the application:

```powershell
dotnet run
```

Or run the compiled executable directly from:
```
bin\Release\net10.0-windows\ChatSharp.exe
```

---

## 📖 How to Use

### Basic Chatting
1. Launch two instances of `ChatSharp` (on the same machine or across a local network).
   - *Instance A*: Listening on Port `3333`.
   - *Instance B*: Change Listening Port to `3334` by entering `3334` and clicking **Change**.
2. In *Instance A*, set **Destination IP** to `127.0.0.1` and **Port** to `3334`.
3. Type a message in the input box and press <kbd>Enter</kbd> (or click **Send**).
4. Both instances negotiate ECDH keys and exchange messages end-to-end encrypted with AES-256-GCM.

### Sending a File
1. Enter the destination IP and port.
2. Click **📎 Send File...** next to the destination port field.
3. Select any file (up to 1 GB).
4. The receiver receives the file name and size, and is prompted to accept or decline the transfer.
5. If accepted, the receiver selects a save location and the binary chunks stream directly to that file. If declined, the transfer is canceled immediately without sending file chunks.

### Exporting Chat History
- Click the **Save Log** button in the top bar (or right-click the log and choose **Save Chat History...**).
- Choose a file path to save a UTF-8 timestamped text log of the conversation.

---

## 📁 Project Structure

```
ChatSharp/
├── Chat.cs              # Main application source (WinForms UI, networking, ECDH & AES-GCM cryptography)
├── ChatSharp.csproj     # .NET 10 project file
├── .gitignore           # Git ignore rules for .NET / Visual Studio
└── README.md            # Project documentation
```

---

## 📜 License

This project is licensed under the [MIT License](LICENSE) (or academic free use).

