package dev.netlift.runtime;

import android.content.Context;
import java.lang.reflect.InvocationTargetException;
import java.lang.reflect.Method;

final class DotnetBootstrap {
    private DotnetBootstrap() {
    }

    static void initialize(Context context) {
        try {
            Class<?> packageManager = Class.forName("mono.MonoPackageManager");
            Method loadApplication =
                packageManager.getMethod("LoadApplication", Context.class);
            loadApplication.invoke(null, context);
        } catch (InvocationTargetException error) {
            Throwable cause = error.getCause();
            if (cause instanceof RuntimeException runtimeError) {
                throw runtimeError;
            }
            if (cause instanceof Error fatalError) {
                throw fatalError;
            }
            throw new IllegalStateException(
                "The .NET Android workload component failed to initialize.",
                cause
            );
        } catch (ReflectiveOperationException error) {
            throw new IllegalStateException(
                "The app-specific .NET Android workload component is missing.",
                error
            );
        }
    }
}
