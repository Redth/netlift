import Foundation
import SwiftUI

struct OrdersMinimalScreen: View {
    let client: NetLiftAsyncClient_2

    @State private var sku = "ABC-123"
    @State private var quantity = 1
    @State private var status = "Ready to place an order."
    @State private var orderTask: Task<Void, Never>?

    var body: some View {
        VStack(spacing: 16) {
            TextField("SKU", text: $sku)
                .textInputAutocapitalization(.characters)
            Stepper("Quantity: \(quantity)", value: $quantity, in: 1...100)
            Button("Place order") {
                orderTask = Task { @MainActor in
                    defer { orderTask = nil }
                    do {
                        let receipt = try await client.placeAsync(
                            draft: OrderDraft(sku: sku, quantity: Int32(quantity)))
                        status = "Order #\(receipt.id): \(receipt.sku) x \(receipt.quantity)"
                    } catch is CancellationError {
                        status = "Order cancelled."
                    } catch {
                        status = "Order failed: \(error.localizedDescription)"
                    }
                }
            }
            .disabled(orderTask != nil || sku.trimmingCharacters(in: .whitespaces).isEmpty)
            if orderTask != nil {
                Button("Cancel order", role: .cancel) {
                    orderTask?.cancel()
                }
            }
            Text(status)
        }
        .padding()
        .onDisappear { orderTask?.cancel() }
    }
}
