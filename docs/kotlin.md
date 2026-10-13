# Kotlin / Android

The complete setup and code now live in the root
[README: Kotlin / Android](../README.md#2-connect-kotlin--android).
It includes the package feed, Gradle configuration, shared C# service,
one-time `Application` initialization, no-argument client, and UI call.

The public plugin is `0.1.0-alpha.14`; it selects runtime `0.1.0-alpha.3`.
Default clients use that runtime for calls and streams. Explicit transport
injection remains available for tests and custom hosts.

For a runnable native Activity, use the
[Hello World sample](../samples/HelloWorld.Android/README.md).
For generated `Flow`, native callbacks, and release details, see the
[product reference](contributing.md#android-gradle-plugin-and-runtime).
