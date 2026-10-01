import groovy.json.JsonOutput
import groovy.json.JsonSlurper
import org.gradle.api.GradleException
import org.gradle.api.tasks.Exec
import org.gradle.api.provider.Provider
import org.gradle.api.file.Directory

plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
    id("org.jetbrains.kotlin.plugin.compose")
}

val repositoryRoot = rootProject.projectDir.resolve("../../..").canonicalFile
val projectionProject =
    repositoryRoot.resolve("src/NetLift.Projection/NetLift.Projection.csproj")
val managedOldProject =
    repositoryRoot.resolve("tests/NetLift.Negotiation.NativeHost.Managed.Old")
val managedNewProject =
    repositoryRoot.resolve("tests/NetLift.Negotiation.NativeHost.Managed.New")
val v5ArtifactRoot = providers.gradleProperty("netliftArtifactsRoot")
val oldPublishOutput = layout.buildDirectory.dir("generated/v5/dotnet/old/publish")
val newPublishOutput = layout.buildDirectory.dir("generated/v5/dotnet/new/publish")
val oldGeneratedSource = layout.buildDirectory.file(
    "generated/v5/dotnet/old/generated/NetLift.Generator/" +
        "NetLift.Generator.LiftContractGenerator/LiftContract.g.cs"
)
val newGeneratedSource = layout.buildDirectory.file(
    "generated/v5/dotnet/new/generated/NetLift.Generator/" +
        "NetLift.Generator.LiftContractGenerator/LiftContract.g.cs"
)
val oldAssets = layout.buildDirectory.dir("generated/v5/android/old/assets")
val newAssets = layout.buildDirectory.dir("generated/v5/android/new/assets")
val oldKotlin = layout.buildDirectory.dir("generated/v5/android/old/kotlin")
val newKotlin = layout.buildDirectory.dir("generated/v5/android/new/kotlin")
val oldHostCodecOutput = layout.buildDirectory.dir("generated/v5/host-codecs/old-peer")
val newHostCodecOutput = layout.buildDirectory.dir("generated/v5/host-codecs/new-peer")

fun registerManagedPublish(
    taskName: String,
    projectFile: File,
    variant: String,
    output: Provider<org.gradle.api.file.Directory>
) = tasks.register<Exec>(taskName) {
    workingDir(repositoryRoot)
    doFirst {
        val root = v5ArtifactRoot.orNull
            ?: throw GradleException(
                "Set -PnetliftArtifactsRoot to a unique session-artifacts directory."
            )
        val variantRoot = File(root, variant)
        val generated = layout.buildDirectory.dir(
            "generated/v5/dotnet/$variant/generated"
        ).get().asFile
        commandLine(
            "dotnet",
            "publish",
            projectFile.relativeTo(repositoryRoot).path,
            "-c",
            "Release",
            "-o",
            output.get().asFile.absolutePath,
            "-p:ArtifactsPath=${File(variantRoot, "artifacts").absolutePath}",
            "-p:CompilerGeneratedFilesOutputPath=${generated.absolutePath}",
            "-p:RestorePackagesPath=${File(variantRoot, "nuget").absolutePath}",
            "-p:EmitCompilerGeneratedFiles=true",
            "-v:minimal"
        )
    }
}

val publishOldManaged = registerManagedPublish(
    "publishV5OldManaged",
    managedOldProject.resolve("NetLift.Negotiation.NativeHost.Managed.Old.csproj"),
    "old",
    oldPublishOutput
)
val publishNewManaged = registerManagedPublish(
    "publishV5NewManaged",
    managedNewProject.resolve("NetLift.Negotiation.NativeHost.Managed.New.csproj"),
    "new",
    newPublishOutput
)

val buildV5HostCodecProjection = tasks.register<Exec>("buildV5HostCodecProjection") {
    workingDir(repositoryRoot)
    doFirst {
        val root = v5ArtifactRoot.orNull
            ?: throw GradleException(
                "Set -PnetliftArtifactsRoot to a unique session-artifacts directory."
            )
        val projectionArtifacts = File(root, "projection")
        commandLine(
            "dotnet",
            "build",
            projectionProject.relativeTo(repositoryRoot).path,
            "-c",
            "Release",
            "--artifacts-path",
            projectionArtifacts.absolutePath,
            "-p:RestorePackagesPath=${File(System.getProperty("user.home"), ".nuget/packages").absolutePath}",
            "--nologo"
        )
        environment("DOTNET_CLI_HOME", File(root, "projection-dotnet-home").absolutePath)
        environment("DOTNET_NOLOGO", "true")
        environment("DOTNET_CLI_TELEMETRY_OPTOUT", "1")
    }
}

val generateV5SampleInputs = tasks.register("generateV5SampleInputs") {
    dependsOn(publishOldManaged, publishNewManaged, buildV5HostCodecProjection)
    outputs.dirs(
        oldAssets,
        newAssets,
        oldKotlin,
        newKotlin,
        oldHostCodecOutput,
        newHostCodecOutput
    )

    doLast {
        oldAssets.get().asFile.deleteRecursively()
        newAssets.get().asFile.deleteRecursively()
        oldKotlin.get().asFile.deleteRecursively()
        newKotlin.get().asFile.deleteRecursively()

        fun canonicalManifest(source: File): String {
            if (!source.isFile)
                throw GradleException("Generated managed contract was not found: $source")
            val sourceText = source.readText()
            val literal = Regex(
                """public const string CanonicalJson = ("(?:\\.|[^"])*");"""
            ).find(sourceText)?.groupValues?.get(1)
                ?: throw GradleException("Generated Contract.CanonicalJson is missing in $source")
            return JsonSlurper().parseText(literal) as String
        }

        fun manifestHash(canonical: String): String {
            val root = JsonSlurper().parseText(canonical) as Map<*, *>
            val hash = root["schemaHash"] as? String
                ?: throw GradleException("Generated managed manifest has no schemaHash.")
            if (!hash.matches(Regex("[0-9a-fA-F]{64}")))
                throw GradleException("Generated managed schemaHash is not a SHA-256 value.")
            return hash.lowercase()
        }

        fun manifestHasOptionalFields(canonical: String): Boolean {
            val root = JsonSlurper().parseText(canonical) as Map<*, *>
            val schema = root["schema"] as? Map<*, *>
                ?: throw GradleException("Generated manifest has no schema.")
            val dto = schema["dto"] as? Map<*, *>
                ?: throw GradleException("Generated manifest has no DTO.")
            val fields = dto["fields"] as? List<*>
                ?: throw GradleException("Generated manifest has no DTO fields.")
            return fields.any { (it as? Map<*, *>)?.get("optional") == true }
        }

        fun stageHostCodec(
            peerContractSource: File,
            expectedPeerHash: String,
            codecOutput: File,
            targetKotlin: File
        ) {
            val artifactRoot = v5ArtifactRoot.orNull
                ?: throw GradleException(
                    "Set -PnetliftArtifactsRoot to a unique session-artifacts directory."
                )
            val projectionAssembly = File(
                artifactRoot,
                "projection/bin/NetLift.Projection/release/NetLift.Projection.dll"
            )
            if (!projectionAssembly.isFile)
                throw GradleException("Schema-v5 projection CLI was not built: $projectionAssembly")

            codecOutput.deleteRecursively()
            codecOutput.mkdirs()
            project.exec {
                workingDir(repositoryRoot)
                commandLine(
                    "dotnet",
                    projectionAssembly.absolutePath,
                    peerContractSource.absolutePath,
                    codecOutput.absolutePath,
                    "--emit-schema-v5-host-codecs"
                )
            }

            val generatedKotlin = File(codecOutput, "NetLift.kt")
            if (!generatedKotlin.isFile)
                throw GradleException("Host-bound Kotlin codec was not generated: $generatedKotlin")
            val generatedSource = generatedKotlin.readText()
            val generatedHash = Regex("schemaHash: String = \\\"([0-9a-f]{64})\\\"")
                .find(generatedSource)
                ?.groupValues
                ?.get(1)
                ?: throw GradleException("Host-bound Kotlin codec has no schema hash.")
            if (generatedHash != expectedPeerHash ||
                !generatedSource.contains("context.requireActive()") ||
                !generatedSource.contains("context.schemaHashHex != schemaHash") ||
                !generatedSource.contains("context.localSchemaHashHex") ||
                generatedSource.contains("testingOnly") ||
                generatedSource.contains("decodePayload")
            ) {
                throw GradleException(
                    "Host-bound Kotlin codec does not match the active peer contract and context."
                )
            }

            val codecTarget = File(
                targetKotlin,
                "com/netlift/androidhost/contract/NetLift.kt"
            )
            codecTarget.parentFile.mkdirs()
            generatedKotlin.copyTo(codecTarget, overwrite = true)
        }

        fun stageVariant(
            variant: String,
            packageName: String,
            assemblyName: String,
            localManifest: String,
            peerManifest: String,
            peerContractSource: File,
            targetAssets: File,
            targetKotlin: File,
            codecOutput: File,
            publishDirectory: File
        ) {
            val managedTarget = File(targetAssets, "netlift/apps/$packageName/dotnet")
            managedTarget.mkdirs()
            val managedAssemblies = publishDirectory.listFiles()
                ?.filter { it.isFile && it.extension == "dll" }
                .orEmpty()
            if (managedAssemblies.none { it.name == "$assemblyName.dll" } ||
                managedAssemblies.none { it.name == "NetLift.Contracts.dll" } ||
                managedAssemblies.none { it.name == "NetLift.Negotiation.dll" }
            ) {
                throw GradleException(
                    "Managed $variant publish output is missing the host or required dependencies."
                )
            }
            managedAssemblies.forEach { it.copyTo(File(managedTarget, it.name), overwrite = true) }

            val managedHash = manifestHash(localManifest)
            val peerHash = manifestHash(peerManifest)
            val peerHasOptionalFields = manifestHasOptionalFields(peerManifest)
            stageHostCodec(peerContractSource, peerHash, codecOutput, targetKotlin)
            val hostConfig = mapOf(
                "managedAssemblyName" to assemblyName,
                "managedAssetDirectory" to "netlift/apps/$packageName/dotnet",
                "expectedManagedSchemaHashHex" to managedHash,
                "localOptIn" to true,
                "minimumPeerMajor" to 1,
                "minimumPeerMinor" to 0,
                "peerManifestJson" to peerManifest,
                "trustedPeerHashHex" to peerHash,
                "trustedMinimumMajor" to 1,
                "trustedMinimumMinor" to 0,
                "peerOptIn" to true
            )
            val appAssets = File(targetAssets, "netlift/apps/$packageName")
            appAssets.mkdirs()
            File(appAssets, "v5-host.json").writeText(
                JsonOutput.prettyPrint(JsonOutput.toJson(hostConfig)) + "\n"
            )

            val kotlinSource = File(
                targetKotlin,
                "com/netlift/v5sample/V5BuildIdentity.kt"
            )
            kotlinSource.parentFile.mkdirs()
            kotlinSource.writeText(
                """
                package com.netlift.v5sample

                internal object V5BuildIdentity {
                    const val expectedManagedSchemaHashHex = "$managedHash"
                    const val nativePeerSchemaHashHex = "$peerHash"
                    const val nativePeerIncludesOptionalFields = $peerHasOptionalFields
                }
                """.trimIndent() + "\n"
            )

            val payloadArguments = if (peerHasOptionalFields) {
                "\"NETLIFT-V5-ANDROID\", 7, \"optional-note\", null, true"
            } else {
                "\"NETLIFT-V5-ANDROID\", 7"
            }
            File(targetKotlin, "com/netlift/v5sample/V5SampleCodec.kt").apply {
                parentFile.mkdirs()
                writeText(
                    """
                    package com.netlift.v5sample

                    import com.netlift.androidhost.contract.NetLiftV5DtoCodec
                    import com.netlift.androidhost.contract.Payload
                    import com.netlift.androidruntime.v5.NegotiatedPeerContext

                    internal object V5SampleCodec {
                        fun encode(context: NegotiatedPeerContext): ByteArray =
                            NetLiftV5DtoCodec.encodePayload(
                                Payload($payloadArguments),
                                context
                            )
                    }
                    """.trimIndent() + "\n"
                )
            }
        }

        val oldManifest = canonicalManifest(oldGeneratedSource.get().asFile)
        val newManifest = canonicalManifest(newGeneratedSource.get().asFile)
        stageVariant(
            "old",
            "com.netlift.v5sample.old",
            "NetLift.Negotiation.NativeHost.Managed.Old",
            oldManifest,
            newManifest,
            newGeneratedSource.get().asFile,
            oldAssets.get().asFile,
            oldKotlin.get().asFile,
            oldHostCodecOutput.get().asFile,
            oldPublishOutput.get().asFile
        )
        stageVariant(
            "new",
            "com.netlift.v5sample.new",
            "NetLift.Negotiation.NativeHost.Managed.New",
            newManifest,
            oldManifest,
            oldGeneratedSource.get().asFile,
            newAssets.get().asFile,
            newKotlin.get().asFile,
            newHostCodecOutput.get().asFile,
            newPublishOutput.get().asFile
        )
    }
}

android {
    namespace = "com.netlift.v5sample"
    compileSdk = 36

    defaultConfig {
        applicationId = "com.netlift.v5sample"
        minSdk = 24
        targetSdk = 36
        versionCode = 1
        versionName = "1.0"
        testInstrumentationRunner = "androidx.test.runner.AndroidJUnitRunner"
        ndk {
            abiFilters += "arm64-v8a"
        }
    }

    flavorDimensions += "managedContract"
    productFlavors {
        create("old") {
            dimension = "managedContract"
            applicationIdSuffix = ".old"
        }
        create("new") {
            dimension = "managedContract"
            applicationIdSuffix = ".new"
        }
    }

    buildFeatures {
        compose = true
        buildConfig = true
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    kotlinOptions {
        jvmTarget = "17"
    }

    buildTypes {
        getByName("release") {
            isMinifyEnabled = true
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"))
        }
    }

    sourceSets {
        getByName("old").assets.srcDir(oldAssets)
        getByName("new").assets.srcDir(newAssets)
    }

    packaging {
        jniLibs {
            useLegacyPackaging = true
        }
    }
}

kotlin.sourceSets.getByName("old").kotlin.srcDir(oldKotlin)
kotlin.sourceSets.getByName("new").kotlin.srcDir(newKotlin)

tasks.named("preBuild").configure {
    dependsOn(generateV5SampleInputs, ":runtime:publishReleasePublicationToSpikeLocalRepository")
}

val runtimeArtifactId =
    rootProject.extra["netliftAndroidRuntimeArtifactId"] as String

dependencies {
    implementation("com.netlift.android:$runtimeArtifactId:0.1.0-spike")
    implementation(platform("androidx.compose:compose-bom:2025.07.00"))
    implementation("androidx.activity:activity-compose:1.10.1")
    implementation("androidx.compose.material3:material3")
    androidTestImplementation("androidx.test:runner:1.6.2")
    androidTestImplementation("androidx.test.ext:junit:1.2.1")
}
