pluginManagement {
    repositories {
        maven {
            url = uri("https://redth.github.io/netlift-sdk/maven/")
        }
        google()
        mavenCentral()
        gradlePluginPortal()
    }
}

dependencyResolutionManagement {
    repositoriesMode.set(RepositoriesMode.FAIL_ON_PROJECT_REPOS)
    repositories {
        maven {
            url = uri("https://redth.github.io/netlift-sdk/maven/")
        }
        google()
        mavenCentral()
    }
}

rootProject.name = "NetLiftWorkloadLibraryMode"
include(":app")
