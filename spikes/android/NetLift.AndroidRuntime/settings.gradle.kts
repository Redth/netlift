pluginManagement {
    repositories {
        google()
        mavenCentral()
        gradlePluginPortal()
    }
}

val isolatedBuildRoot = providers.gradleProperty("netliftBuildRoot")
    .orNull
    ?.let { java.io.File(it) }

dependencyResolutionManagement {
    repositoriesMode.set(RepositoriesMode.FAIL_ON_PROJECT_REPOS)
    repositories {
        maven {
            url = uri(
                isolatedBuildRoot?.resolve("runtime/local-maven")
                    ?: java.io.File(settingsDir, "runtime/build/local-maven")
            )
        }
        google()
        mavenCentral()
    }
}

rootProject.name = "NetLift.AndroidRuntime"
include(":runtime", ":sample", ":second-sample", ":v5-sample", ":v5-async-sample")
