# WebSocket Tester

A Windows-only .NET MAUI app for trying out WebSocket servers with the `WebSockets` library: pick a URL and
subprotocols, connect, send text or binary messages, close with a code and reason (or drop the connection),
and watch the `open`, `message`, `error` and `close` events alongside `readyState` and `bufferedAmount`.

Build a self-contained, unpackaged exe (no .NET or Windows App SDK runtime needed on the target machine):

```
dotnet publish samples/WebSocketTester -c Release
```

The output is `samples/WebSocketTester/bin/Release/net10.0-windows10.0.19041.0/win-x64/publish/WebSocketTester.exe`;
copy the whole `publish` folder to run it elsewhere. Building needs the `maui-windows` workload on Windows.
The project is not in `WebSockets.slnx`, so the Linux CI build is unaffected.
