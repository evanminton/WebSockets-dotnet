namespace WebSocketTester;

public partial class App : Application
{
	public App()
	{
		InitializeComponent();
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		return new Window(new AppShell()) { Title = "WebSocket Tester", Width = 1000, Height = 760 };
	}
}
