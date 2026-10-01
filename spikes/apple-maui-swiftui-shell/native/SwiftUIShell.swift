import SwiftUI
import UIKit

public typealias ManagedAction = @convention(c) (UnsafeMutableRawPointer?, Int32) -> Void

// This pointer is read and written only by the synchronous main-actor closure.
private final class OpaquePointerBox: @unchecked Sendable {
	var value: UnsafeMutableRawPointer?
}

@MainActor
private final class SwiftUIShellState: ObservableObject {
	@Published var resultText = "Tap an action to call the managed service."
	@Published var callbackText = "No callback yet."

	let managedAction: ManagedAction
	let runtimeText: String
	var context: UnsafeMutableRawPointer?
	weak var controller: UIViewController?
	private var isClosing = false

	init(managedAction: ManagedAction, runtimeText: String) {
		self.managedAction = managedAction
		self.runtimeText = runtimeText
	}

	func invoke(_ action: Int32) {
		managedAction(context, action)
	}

	func showConfirmation(accepted: Int32, sequence: Int32, managedOnMain: Int32) {
		resultText = accepted == 1 ? "Accepted" : "Declined"
		callbackText = "Managed callback #\(sequence): main=\(managedOnMain == 1), Swift main=\(Thread.isMainThread)"
	}

	func close() {
		guard !isClosing else { return }
		isClosing = true
		controller?.dismiss(animated: true) { [context] in
			if let context {
				Unmanaged<SwiftUIShellState>.fromOpaque(context).release()
			}
		}
	}
}

@MainActor
private struct SwiftUIShellView: View {
	@ObservedObject var state: SwiftUIShellState

	var body: some View {
		VStack(spacing: 20) {
			Text("SwiftUI owns this screen")
				.font(.largeTitle)
				.multilineTextAlignment(.center)
			Text(state.resultText)
				.font(.title2)
			Text(state.callbackText)
				.font(.footnote)
				.multilineTextAlignment(.center)
			Text(state.runtimeText)
				.font(.caption)
				.multilineTextAlignment(.center)
			HStack(spacing: 20) {
				Button("Accept") { state.invoke(1) }
				Button("Decline") { state.invoke(0) }
			}
			Button("Close") { state.invoke(2) }
		}
		.padding(32)
		.frame(maxWidth: .infinity, maxHeight: .infinity)
		.background(Color(uiColor: .systemBackground))
	}
}

private func topViewController() -> UIViewController? {
	let keyWindow = UIApplication.shared.connectedScenes
		.compactMap { $0 as? UIWindowScene }
		.flatMap(\.windows)
		.first(where: \.isKeyWindow)

	var controller = keyWindow?.rootViewController
	while let presented = controller?.presentedViewController {
		controller = presented
	}
	return controller
}

@_cdecl("nl_maui_swiftui_present")
public func presentSwiftUIShell(
	_ managedAction: ManagedAction,
	_ runtimeProbe: UnsafePointer<CChar>?
) -> UnsafeMutableRawPointer? {
	let runtimeText = runtimeProbe.map(String.init(cString:)) ?? "Runtime probe unavailable."
	let contextBox = OpaquePointerBox()
	MainActor.assumeIsolated {
		guard let presenter = topViewController() else {
			return
		}

		let state = SwiftUIShellState(managedAction: managedAction, runtimeText: runtimeText)
		let context = Unmanaged.passRetained(state).toOpaque()
		contextBox.value = context
		state.context = context

		let controller = UIHostingController(rootView: SwiftUIShellView(state: state))
		controller.modalPresentationStyle = .fullScreen
		state.controller = controller
		presenter.present(controller, animated: true)
	}
	return contextBox.value
}

@_cdecl("nl_maui_swiftui_complete")
public func completeSwiftUIShell(
	_ context: UnsafeMutableRawPointer?,
	_ accepted: Int32,
	_ sequence: Int32,
	_ managedCallbackOnMainThread: Int32
) {
	guard let context else { return }
	let state = Unmanaged<SwiftUIShellState>.fromOpaque(context).takeUnretainedValue()

	let update = {
		MainActor.assumeIsolated {
			state.showConfirmation(
				accepted: accepted,
				sequence: sequence,
				managedOnMain: managedCallbackOnMainThread)
		}
	}
	if Thread.isMainThread {
		update()
	} else {
		DispatchQueue.main.async(execute: update)
	}
}

@_cdecl("nl_maui_swiftui_close")
public func closeSwiftUIShell(_ context: UnsafeMutableRawPointer?) {
	guard let context else { return }
	let state = Unmanaged<SwiftUIShellState>.fromOpaque(context).takeUnretainedValue()

	let dismiss = {
		MainActor.assumeIsolated {
			state.close()
		}
	}
	if Thread.isMainThread {
		dismiss()
	} else {
		DispatchQueue.main.async(execute: dismiss)
	}
}
