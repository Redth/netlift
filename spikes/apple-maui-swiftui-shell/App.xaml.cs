using Microsoft.Extensions.DependencyInjection;

namespace NetLift.MauiSwiftUIShell;

public partial class App : Application
{
	public App()
	{
		InitializeComponent();
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		return new Window(new MainPage());
	}
}