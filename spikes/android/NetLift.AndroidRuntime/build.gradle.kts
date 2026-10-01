plugins {
    id("com.android.library") version "8.11.1" apply false
    id("com.android.application") version "8.11.1" apply false
    id("org.jetbrains.kotlin.android") version "2.2.20" apply false
    id("org.jetbrains.kotlin.plugin.compose") version "2.2.20" apply false
}

val sourceV2LifecycleProperty =
    providers.gradleProperty("netliftAndroidSourceV2Lifecycle").orElse("false").get()
val useSourceV2Lifecycle = when (sourceV2LifecycleProperty.lowercase()) {
    "true" -> true
    "false" -> false
    else -> throw GradleException(
        "netliftAndroidSourceV2Lifecycle must be either true or false."
    )
}
extra["netliftAndroidUseSourceV2Lifecycle"] = useSourceV2Lifecycle
extra["netliftAndroidRuntimeArtifactId"] =
    if (useSourceV2Lifecycle) {
        "netlift-android-runtime-source-v2-test"
    } else {
        "netlift-android-runtime"
    }

val isolatedBuildRoot = providers.gradleProperty("netliftBuildRoot")
    .orNull
    ?.let { java.io.File(it) }

if (isolatedBuildRoot != null) {
    allprojects {
        val relativePath = if (path == ":") {
            "root"
        } else {
            path.removePrefix(":").replace(':', '/')
        }
        layout.buildDirectory.set(isolatedBuildRoot.resolve(relativePath))
    }
}
