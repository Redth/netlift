import netlift.spikes.gradle.StageV5AsyncCodecTask

plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

val repositoryRoot = file(providers.gradleProperty("netliftRepositoryRoot").get())
val artifactRoot = file(providers.gradleProperty("netliftAsyncArtifacts").get())
val selectedDirection = providers.gradleProperty("netliftAsyncDirection").orElse("old").get()
require(selectedDirection == "old" || selectedDirection == "new") {
    "netliftAsyncDirection must be old or new."
}

layout.buildDirectory.set(artifactRoot.resolve("gradle-build/app"))

android {
    namespace = "com.netlift.androidhost"
    compileSdk = 36

    defaultConfig {
        applicationId = "com.netlift.androidhost.asyncfixture"
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
        val sourceDirectory = artifactRoot.resolve("seed/$selectedDirection")
        val taskProvider = tasks.register<StageV5AsyncCodecTask>(
            "stage${taskSuffix}NetLiftV5AsyncCodec"
        ) {
            this.direction.set(selectedDirection)
            variantName.set(variant.name)
            codecLock.set(artifactRoot.resolve("codec-lock.json"))
            generatedCodecs.from(
                sourceDirectory.resolve("NetLift.swift"),
                sourceDirectory.resolve("NetLift.kt")
            )
            sourceDirectoryPath.set(sourceDirectory.absolutePath)
            consumerTemplate.set(
                repositoryRoot.resolve(
                    "spikes/build-dev-loop/V5AsyncNativeConsumer/Android/CodecConsumer.$selectedDirection.kt"
                )
            )
            stageScript.set(
                repositoryRoot.resolve(
                    "spikes/build-dev-loop/V5AsyncNativeConsumer/stage-async-codec.py"
                )
            )
            kotlinOutputDirectory.set(
                layout.buildDirectory.dir("generated/java/stage${taskSuffix}NetLiftV5AsyncCodec")
            )
        }

        variant.sources.java?.addGeneratedSourceDirectory(
            taskProvider,
            StageV5AsyncCodecTask::kotlinOutputDirectory
        )
    }
}
