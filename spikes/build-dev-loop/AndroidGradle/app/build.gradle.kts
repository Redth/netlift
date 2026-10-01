import netlift.spikes.gradle.GenerateNetLiftProjectionTask

plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

val repositoryRoot = rootProject.projectDir.resolve("../../..").canonicalFile
val spikeArtifacts = rootProject.projectDir.resolve("../.artifacts/android-gradle")
val fixturePath = providers.gradleProperty("netliftFixture").get()

layout.buildDirectory.set(spikeArtifacts.resolve("build/app"))

android {
    namespace = "com.netlift.androidhost"
    compileSdk = 36

    defaultConfig {
        applicationId = "com.netlift.androidhost"
        minSdk = 24
        targetSdk = 35
        versionCode = 1
        versionName = "1.0"
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    kotlinOptions {
        jvmTarget = "17"
    }
}

androidComponents {
    onVariants(selector().all()) { variant ->
        val taskSuffix = variant.name.replaceFirstChar { it.uppercase() }
        val projectionTask = tasks.register<GenerateNetLiftProjectionTask>(
            "generate${taskSuffix}NetLiftProjection"
        ) {
            variantName.set(variant.name)
            dotnetCommand.set(providers.gradleProperty("netliftDotnet").orElse("dotnet"))
            contractFixture.set(file(fixturePath))
            projectionProject.set(repositoryRoot.resolve("src/NetLift.Projection/NetLift.Projection.csproj"))
            projectionSource.set(repositoryRoot.resolve("src/NetLift.Projection/Program.cs"))
            sdkPin.set(repositoryRoot.resolve("global.json"))
            dotnetHome.set(spikeArtifacts.resolve("dotnet-home"))
            nugetPackageCache.set(spikeArtifacts.resolve("nuget-packages"))
            managedArtifactsDirectory.set(
                layout.buildDirectory.dir("netlift/managed-cli/${variant.name}")
            )
        }

        variant.sources.java?.addGeneratedSourceDirectory(
            projectionTask,
            GenerateNetLiftProjectionTask::kotlinOutputDirectory
        )
    }
}

dependencies {
    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-core:1.10.2")
}
