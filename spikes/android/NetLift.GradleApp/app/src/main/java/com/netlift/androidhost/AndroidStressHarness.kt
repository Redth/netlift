package com.netlift.androidhost

import kotlinx.coroutines.CompletableDeferred

internal object AndroidStressHarness {
    enum class ConfirmationMode {
        INTERACTIVE,
        WAIT_FOR_PROMPT,
        ACCEPT,
        DECLINE
    }

    private val gate = Any()
    private var confirmationMode = ConfirmationMode.INTERACTIVE
    private var promptVisible = CompletableDeferred<Unit>()
    private var activityReady = CompletableDeferred<MainActivity>()

    fun waitForPrompt() {
        synchronized(gate) {
            confirmationMode = ConfirmationMode.WAIT_FOR_PROMPT
            promptVisible = CompletableDeferred()
        }
    }

    fun setDecision(accepted: Boolean) {
        synchronized(gate) {
            confirmationMode = if (accepted) ConfirmationMode.ACCEPT else ConfirmationMode.DECLINE
        }
    }

    fun setInteractive() {
        synchronized(gate) {
            confirmationMode = ConfirmationMode.INTERACTIVE
        }
    }

    fun confirmationMode(): ConfirmationMode = synchronized(gate) { confirmationMode }

    fun onPromptVisible() {
        synchronized(gate) { promptVisible }.complete(Unit)
    }

    suspend fun awaitPrompt() {
        synchronized(gate) { promptVisible }.await()
    }

    fun expectActivityRecreation() {
        synchronized(gate) {
            activityReady = CompletableDeferred()
        }
    }

    fun onActivityReady(activity: MainActivity) {
        synchronized(gate) { activityReady }.complete(activity)
    }

    suspend fun awaitActivity(): MainActivity =
        synchronized(gate) { activityReady }.await()
}
