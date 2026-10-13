pluginManagement {
    val netliftLocalRepository =
        providers.gradleProperty("netliftLocalRepository").orNull
    repositories {
        if (netliftLocalRepository != null) {
            maven {
                url = uri(netliftLocalRepository)
            }
        }
        maven {
            url = uri("https://redth.github.io/netlift-sdk/maven/")
        }
        google()
        mavenCentral()
        gradlePluginPortal()
    }
}

dependencyResolutionManagement {
    val netliftLocalRepository =
        providers.gradleProperty("netliftLocalRepository").orNull
    repositoriesMode.set(RepositoriesMode.FAIL_ON_PROJECT_REPOS)
    repositories {
        if (netliftLocalRepository != null) {
            maven {
                url = uri(netliftLocalRepository)
            }
        }
        maven {
            url = uri("https://redth.github.io/netlift-sdk/maven/")
        }
        google()
        mavenCentral()
    }
}

rootProject.name = "NetLiftWorkloadLibraryMode"
include(":app")
