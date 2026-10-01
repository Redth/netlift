using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Maui.ApplicationModel;
using NetLift.MauiSwiftUIShell.Business;

namespace NetLift.MauiSwiftUIShell;

internal static class NativeSwiftUIHost
{
	private static readonly SubmitService SubmitService = new();

	[DllImport("__Internal", EntryPoint = "nl_maui_swiftui_present")]
	private static extern IntPtr PresentNativeSwiftUI(
		IntPtr callback,
		[MarshalAs(UnmanagedType.LPUTF8Str)] string runtimeProbe);

	[DllImport("__Internal", EntryPoint = "nl_maui_swiftui_complete")]
	private static extern void CompleteNativeSwiftUI(
		IntPtr context,
		int accepted,
		int sequence,
		int managedCallbackOnMainThread);

	[DllImport("__Internal", EntryPoint = "nl_maui_swiftui_close")]
	private static extern void CloseNativeSwiftUI(IntPtr context);

	internal static unsafe IntPtr Present()
	{
		var runtimeProbe =
			$"Mono={Type.GetType("Mono.Runtime") is not null}; " +
			$"{RuntimeInformation.FrameworkDescription}; " +
			$"dynamic-code-supported={RuntimeFeature.IsDynamicCodeSupported}; " +
			$"dynamic-code-compiled={RuntimeFeature.IsDynamicCodeCompiled}; " +
			"MAUI .NET 11 iOS CoreCLR interpreter policy=enabled";
		Console.WriteLine($"RUNTIME_PROBE {runtimeProbe}");
		delegate* unmanaged[Cdecl]<IntPtr, int, void> callback = &OnNativeAction;
		return PresentNativeSwiftUI((IntPtr)callback, runtimeProbe);
	}

	[UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
	private static void OnNativeAction(IntPtr context, int action)
	{
		try
		{
			if (action == 2)
			{
				CloseNativeSwiftUI(context);
				Console.WriteLine("SWIFTUI_HOST_CLOSED");
				return;
			}

			if (action is not (0 or 1))
			{
				throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown SwiftUI action.");
			}

			var result = SubmitService.Submit(action == 1, new SwiftUIConfirmation(context));
			Console.WriteLine(
				$"MANAGED_SUBMIT_RESULT accepted={result.Accepted} sequence={result.Sequence} main={MainThread.IsMainThread}");
		}
		catch (Exception exception)
		{
			Console.Error.WriteLine($"SWIFTUI_MANAGED_CALLBACK_FAILED: {exception}");
		}
	}

	private sealed class SwiftUIConfirmation(IntPtr context) : ISubmitConfirmation
	{
		public void Confirm(SubmitResult result)
		{
			CompleteNativeSwiftUI(
				context,
				result.Accepted ? 1 : 0,
				result.Sequence,
				MainThread.IsMainThread ? 1 : 0);
		}
	}
}
