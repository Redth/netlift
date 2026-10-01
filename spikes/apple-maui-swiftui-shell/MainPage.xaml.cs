namespace NetLift.MauiSwiftUIShell;

public partial class MainPage : ContentPage
{
	private bool _didPresentNativeHost;

	public MainPage()
	{
		InitializeComponent();
	}

	protected override void OnAppearing()
	{
		base.OnAppearing();

		if (_didPresentNativeHost)
		{
			return;
		}

		_didPresentNativeHost = true;
		if (NativeSwiftUIHost.Present() == IntPtr.Zero)
		{
			Content = new Label
			{
				Text = "SwiftUI host could not find a presenter.",
				TextColor = Colors.White,
				HorizontalOptions = LayoutOptions.Center,
				VerticalOptions = LayoutOptions.Center
			};
		}
	}
}
