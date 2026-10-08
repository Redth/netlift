package dev.netlift.workloadlibrarymode;

import android.content.Context;

public final class DotnetAndroidRuntime {
    private enum State {
        UNINITIALIZED,
        INITIALIZING,
        READY,
        FAILED
    }

    private static State state = State.UNINITIALIZED;
    private static Throwable failure;

    private DotnetAndroidRuntime() {
    }

    public static synchronized void initialize(Context context) {
        if (context == null) {
            throw new IllegalArgumentException("context");
        }
        if (state == State.READY) {
            return;
        }
        if (state == State.FAILED) {
            throw new IllegalStateException(
                "The .NET Android runtime previously failed to initialize.",
                failure
            );
        }
        if (state == State.INITIALIZING) {
            throw new IllegalStateException(
                "Reentrant .NET Android runtime initialization is not supported."
            );
        }

        state = State.INITIALIZING;
        try {
            Context applicationContext = context.getApplicationContext();
            mono.MonoPackageManager.LoadApplication(
                applicationContext != null ? applicationContext : context
            );
            state = State.READY;
        } catch (Throwable error) {
            failure = error;
            state = State.FAILED;
            throw error;
        }
    }

    public static synchronized boolean isInitialized() {
        return state == State.READY;
    }

    public static synchronized Throwable getInitializationFailure() {
        return failure;
    }
}
