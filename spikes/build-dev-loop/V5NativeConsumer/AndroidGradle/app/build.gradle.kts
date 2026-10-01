import netlift.spikes.gradle.GenerateNetLiftV5CodecTask

plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

val repositoryRoot = file(providers.gradleProperty("netliftRepositoryRoot").get())
val artifactRoot = file(providers.gradleProperty("netliftNativeGraphArtifacts").get())
val direction = providers.gradleProperty("netliftV5Projection").orElse("old").get()
require(direction == "old" || direction == "new") {
    "netliftV5Projection must be old or new."
}

layout.buildDirectory.set(artifactRoot.resolve("gradle-build/app"))

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
        val generatedContract = artifactRoot.resolve(
            "inputs/contracts/$direction/LiftContract.g.cs"
        )
        val taskProvider = tasks.register<GenerateNetLiftV5CodecTask>(
            "generate${taskSuffix}NetLiftV5Codec"
        ) {
            selectedDirection.set(direction)
            variantName.set(variant.name)
            codecSchemaLock.set(artifactRoot.resolve("inputs/codec-schema.lock"))
            contractSource.set(generatedContract)
            projectionAssembly.set(
                artifactRoot.resolve(
                    "projection-artifacts/bin/NetLift.Projection/release/NetLift.Projection.dll"
                )
            )
            projectionSourceFingerprint.set(
                artifactRoot.resolve("inputs/projection-source.sha256")
            )
            repositoryRootPath.set(repositoryRoot.absolutePath)
            artifactsRootPath.set(artifactRoot.absolutePath)
            stageScript.set(
                repositoryRoot.resolve("spikes/build-dev-loop/V5NativeConsumer/stage-v5-codec.sh")
            )
            consumerTemplate.set(
                repositoryRoot.resolve(
                    "spikes/build-dev-loop/V5NativeConsumer/AndroidGradle/Consumers/CodecConsumer.$direction.kt"
                )
            )
            kotlinOutputDirectory.set(
                layout.buildDirectory.dir("generated/java/generate${taskSuffix}NetLiftV5Codec")
            )
        }

        variant.sources.java?.addGeneratedSourceDirectory(
            taskProvider,
            GenerateNetLiftV5CodecTask::kotlinOutputDirectory
        )
    }
}

dependencies {
    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-core:1.10.2")
}
