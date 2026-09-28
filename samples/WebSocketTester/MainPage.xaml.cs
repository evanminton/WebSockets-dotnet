using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using WebSockets;
using WebSocket = WebSockets.WebSocket;

namespace WebSocketTester;

public partial class MainPage : ContentPage
{
	private const string SendText = "Text";
	private const string SendHex = "Binary (hex bytes)";
	private const string SendUtf8Bytes = "Binary (UTF-8 bytes)";

	private static readonly Color InfoColor = Colors.Gray;
	private static readonly Color OpenColor = Colors.SeaGreen;
	private static readonly Color SentColor = Colors.SteelBlue;
	private static readonly Color ReceivedColor = Colors.MediumPurple;
	private static readonly Color ErrorColor = Colors.IndianRed;
	private static readonly Color CloseColor = Colors.DarkOrange;

	private readonly IDispatcherTimer _stateTimer;
	private WebSocket? _socket;

	public MainPage()
	{
		InitializeComponent();
		BindingContext = this;

		SendModePicker.ItemsSource = new[] { SendText, SendHex, SendUtf8Bytes };
		SendModePicker.SelectedIndex = 0;
		BinaryTypePicker.ItemsSource = new[] { nameof(BinaryType.Blob), nameof(BinaryType.ArrayBuffer) };
		BinaryTypePicker.SelectedIndex = 0;

		// bufferedAmount goes down as the send loop drains, with no event for it, so poll it.
		_stateTimer = Dispatcher.CreateTimer();
		_stateTimer.Interval = TimeSpan.FromMilliseconds(100);
		_stateTimer.Tick += (_, _) => RefreshState();
		_stateTimer.Start();
		RefreshState();
	}

	public ObservableCollection<LogEntry> Log { get; } = [];

	private void OnConnectClicked(object? sender, EventArgs e)
	{
		if (_socket is { ReadyState: WebSocketReadyState.Connecting or WebSocketReadyState.Open or WebSocketReadyState.Closing })
		{
			AddLog("info", "Already connected; close or drop the current WebSocket first.", InfoColor);
			return;
		}

		string url = UrlEntry.Text?.Trim() ?? "";
		string[] protocols = (ProtocolsEntry.Text ?? "")
			.Split([',', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

		WebSocket socket;
		try
		{
			// Constructed on the UI thread, so events are dispatched on it too.
			socket = new WebSocket(url, protocols, new WebSocketOptions
			{
				EventHandlerException = ex => AddLog("error", $"Event handler threw: {ex.Message}", ErrorColor),
			});
		}
		catch (DomException ex)
		{
			AddLog("throw", $"{ex.Name}: {ex.Message}", ErrorColor);
			return;
		}

		socket.BinaryType = SelectedBinaryType;
		socket.OnOpen += OnSocketOpen;
		socket.OnMessage += OnSocketMessage;
		socket.OnError += OnSocketError;
		socket.OnClose += OnSocketClose;
		_socket = socket;

		string requested = protocols.Length == 0 ? "no subprotocols" : "subprotocols " + string.Join(", ", protocols);
		AddLog("connect", $"new WebSocket(\"{socket.Url}\") with {requested}", InfoColor);
		RefreshState();
	}

	private void OnDropClicked(object? sender, EventArgs e)
	{
		if (_socket is null)
		{
			AddLog("info", "No WebSocket to drop.", InfoColor);
			return;
		}

		AddLog("drop", "Dispose(): dropping the connection without a closing handshake", InfoColor);
		_socket.Dispose();
		RefreshState();
	}

	private void OnSendClicked(object? sender, EventArgs e)
	{
		if (_socket is null)
		{
			AddLog("info", "Connect first.", InfoColor);
			return;
		}

		string input = MessageEditor.Text ?? "";
		string mode = SendModePicker.SelectedItem as string ?? SendText;
		try
		{
			switch (mode)
			{
				case SendHex:
					if (!TryParseHex(input, out byte[] hexBytes))
					{
						AddLog("info", "Not valid hex: use pairs of hex digits, optionally separated by spaces, commas or dashes.", ErrorColor);
						return;
					}

					_socket.Send(hexBytes);
					AddLog("sent", $"binary, {Describe(hexBytes)}", SentColor);
					break;

				case SendUtf8Bytes:
					byte[] utf8 = Encoding.UTF8.GetBytes(input);
					_socket.Send(utf8);
					AddLog("sent", $"binary, {Describe(utf8)}", SentColor);
					break;

				default:
					_socket.Send(input);
					AddLog("sent", $"text, {Encoding.UTF8.GetByteCount(input)} bytes: {Shorten(input)}", SentColor);
					break;
			}
		}
		catch (DomException ex)
		{
			AddLog("throw", $"{ex.Name}: {ex.Message}", ErrorColor);
		}

		RefreshState();
	}

	private void OnCloseClicked(object? sender, EventArgs e)
	{
		if (_socket is null)
		{
			AddLog("info", "No WebSocket to close.", InfoColor);
			return;
		}

		string codeText = CloseCodeEntry.Text?.Trim() ?? "";
		string reason = CloseReasonEntry.Text ?? "";
		ushort? code = null;
		if (codeText.Length > 0)
		{
			if (!ushort.TryParse(codeText, NumberStyles.None, CultureInfo.InvariantCulture, out ushort parsed))
			{
				AddLog("info", $"'{codeText}' is not a close code.", ErrorColor);
				return;
			}

			code = parsed;
		}

		// As in the browser API, a reason is only sent together with a code.
		string? reasonArg = code is null || reason.Length == 0 ? null : reason;
		string call = code is null ? "close()" : reasonArg is null ? $"close({code})" : $"close({code}, \"{reasonArg}\")";
		try
		{
			_socket.Close(code, reasonArg);
			AddLog("close()", call, InfoColor);
		}
		catch (DomException ex)
		{
			AddLog("throw", $"{call}: {ex.Name}: {ex.Message}", ErrorColor);
		}

		RefreshState();
	}

	private void OnBinaryTypeChanged(object? sender, EventArgs e)
	{
		if (_socket is not null)
		{
			_socket.BinaryType = SelectedBinaryType;
		}
	}

	private void OnClearClicked(object? sender, EventArgs e) => Log.Clear();

	private void OnSocketOpen(object? sender, EventArgs e)
	{
		if (sender is not WebSocket socket)
		{
			return;
		}

		string protocol = socket.Protocol.Length == 0 ? "none" : socket.Protocol;
		string extensions = socket.Extensions.Length == 0 ? "none" : socket.Extensions;
		AddLog("open", $"protocol: {protocol}; extensions: {extensions}", OpenColor);
		RefreshState();
	}

	private void OnSocketMessage(object? sender, MessageEventArgs e)
	{
		string text = e.Data switch
		{
			string s => $"text, {Encoding.UTF8.GetByteCount(s)} bytes: {Shorten(s)}",
			Blob blob => $"binary as Blob, {Describe(blob.Memory.Span)}",
			byte[] bytes => $"binary as byte[], {Describe(bytes)}",
			var other => other.ToString() ?? "",
		};
		AddLog("message", text, ReceivedColor);
	}

	private void OnSocketError(object? sender, EventArgs e)
	{
		AddLog("error", "error event", ErrorColor);
		RefreshState();
	}

	private void OnSocketClose(object? sender, CloseEventArgs e)
	{
		string reason = e.Reason.Length == 0 ? "" : $", reason \"{e.Reason}\"";
		AddLog("close", $"code {e.Code}{reason}, wasClean {e.WasClean.ToString().ToLowerInvariant()}", CloseColor);
		RefreshState();
	}

	private BinaryType SelectedBinaryType =>
		BinaryTypePicker.SelectedItem as string == nameof(BinaryType.ArrayBuffer) ? BinaryType.ArrayBuffer : BinaryType.Blob;

	private void RefreshState()
	{
		if (_socket is null)
		{
			ReadyStateLabel.Text = "readyState: —";
			BufferedAmountLabel.Text = "bufferedAmount: 0";
			ProtocolLabel.Text = "protocol: —";
			ExtensionsLabel.Text = "extensions: —";
			return;
		}

		WebSocketReadyState state = _socket.ReadyState;
		ReadyStateLabel.Text = $"readyState: {(int)state} {state.ToString().ToUpperInvariant()}";
		BufferedAmountLabel.Text = $"bufferedAmount: {_socket.BufferedAmount}";
		ProtocolLabel.Text = $"protocol: {(_socket.Protocol.Length == 0 ? "\"\"" : _socket.Protocol)}";
		ExtensionsLabel.Text = $"extensions: {(_socket.Extensions.Length == 0 ? "\"\"" : _socket.Extensions)}";
	}

	private void AddLog(string kind, string text, Color color)
	{
		string time = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
		Log.Add(new LogEntry(time, kind, text, color));
	}

	private static string Shorten(string text)
	{
		const int MaxShown = 1000;
		return text.Length > MaxShown ? text[..MaxShown] + " …" : text;
	}

	private static string Describe(ReadOnlySpan<byte> bytes)
	{
		const int MaxShown = 64;
		string hex = Convert.ToHexString(bytes[..Math.Min(bytes.Length, MaxShown)]).ToLowerInvariant();
		var spaced = new StringBuilder(hex.Length * 3 / 2);
		for (int i = 0; i < hex.Length; i += 2)
		{
			if (i > 0)
			{
				spaced.Append(' ');
			}

			spaced.Append(hex, i, 2);
		}

		string more = bytes.Length > MaxShown ? " …" : "";
		return $"{bytes.Length} bytes: {spaced}{more}";
	}

	private static bool TryParseHex(string input, out byte[] bytes)
	{
		var digits = new StringBuilder(input.Length);
		foreach (char c in input)
		{
			if (!char.IsWhiteSpace(c) && c is not (',' or '-' or ':'))
			{
				digits.Append(c);
			}
		}

		string text = digits.ToString();
		if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
		{
			text = text[2..];
		}

		bytes = [];
		if (text.Length % 2 != 0)
		{
			return false;
		}

		try
		{
			bytes = Convert.FromHexString(text);
			return true;
		}
		catch (FormatException)
		{
			return false;
		}
	}
}
