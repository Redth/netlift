pluginManagement {
    repositories {
        google()
        mavenCentral()
        gradlePluginPortal()
    }
}

dependencyResolutionManagement {
    repositoriesMode.set(RepositoriesMode.FAIL_ON_PROJECT_REPOS)
    repositories {
        maven {
            url = uri("../../../../product/artifacts/maven")
        }
        google()
        mavenCentral()
    }
}

rootProject.name = "NetLiftWorkloadLibraryMode"
include(":app")
