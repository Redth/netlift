package com.netlift.v5asyncsample

import org.junit.Assert.assertEquals
import org.junit.Test

class AsyncSampleUiStateTest {
    @Test
    fun replyAcknowledgementDoesNotOverwriteCompletedOperation() {
        val activeState = activeState()
        val terminalMessage = "Task completed: code=7, accepted=true, audited=null"
        val terminalState = completeOperationIfActive(activeState, 41, terminalMessage)

        assertEquals(
            terminalState,
            acknowledgeReplyIfOperationActive(
                terminalState,
                operationToken = 41,
                requestToken = 73,
                accepted = true
            )
        )
    }

    @Test
    fun terminalResultReplacesInterimReplyAcknowledgement() {
        val acknowledged = acknowledgeReplyIfOperationActive(
            activeState(),
            operationToken = 41,
            requestToken = 73,
            accepted = true
        )

        assertEquals(
            AsyncSampleUiState(message = "Task completed: code=7, accepted=true, audited=null"),
            completeOperationIfActive(
                acknowledged,
                operationToken = 41,
                message = "Task completed: code=7, accepted=true, audited=null"
            )
        )
    }

    @Test
    fun replyAcknowledgementOnlyClearsMatchingActiveRequest() {
        val activeState = activeState()

        assertEquals(
            activeState.copy(
                message = "Approval replied.",
                pendingRequestToken = null,
                confirmationText = ""
            ),
            acknowledgeReplyIfOperationActive(
                activeState,
                operationToken = 41,
                requestToken = 73,
                accepted = true
            )
        )
        assertEquals(
            activeState,
            acknowledgeReplyIfOperationActive(
                activeState,
                operationToken = 42,
                requestToken = 73,
                accepted = true
            )
        )
    }

    @Test
    fun replyFailureMessageOnlyUpdatesMatchingPendingRequest() {
        val activeState = activeState()
        val failureMessage =
            "Could not reply to ConfirmAsync: The async session is not available."

        assertEquals(
            activeState.copy(message = failureMessage),
            updateRequestMessageIfActive(
                activeState,
                operationToken = 41,
                requestToken = 73,
                message = failureMessage
            )
        )
        assertEquals(
            activeState,
            updateRequestMessageIfActive(
                activeState,
                operationToken = 42,
                requestToken = 73,
                message = failureMessage
            )
        )
        assertEquals(
            activeState,
            updateRequestMessageIfActive(
                activeState,
                operationToken = 41,
                requestToken = 74,
                message = failureMessage
            )
        )
    }

    @Test
    fun cancellationAcknowledgementDoesNotOverwriteCompletedOperation() {
        val terminalMessage = "Task cancelled."
        val terminalState = completeOperationIfActive(activeState(), 41, terminalMessage)

        assertEquals(
            terminalState,
            updateOperationMessageIfActive(
                terminalState,
                operationToken = 41,
                message = "Cancellation requested; awaiting terminal result."
            )
        )
    }

    @Test
    fun terminalResultReplacesCancellationAcknowledgement() {
        val terminalMessage = "Task cancelled."
        val acknowledged = updateOperationMessageIfActive(
            activeState(),
            operationToken = 41,
            message = "Cancellation requested; awaiting terminal result."
        )

        assertEquals(
            AsyncSampleUiState(message = terminalMessage),
            completeOperationIfActive(acknowledged, 41, terminalMessage)
        )
    }

    private fun activeState() = AsyncSampleUiState(
        message = "Managed ConfirmAsync is waiting for a typed reply.",
        pendingRequestToken = 73,
        confirmationText = "Approve this order?",
        activeOperationToken = 41
    )
}
