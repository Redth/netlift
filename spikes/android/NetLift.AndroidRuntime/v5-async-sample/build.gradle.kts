import groovy.json.JsonOutput
import groovy.json.JsonSlurper
import org.gradle.api.GradleException
import org.gradle.api.tasks.Exec
import org.gradle.api.provider.Provider

plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
    id("org.jetbrains.kotlin.plugin.compose")
}

val repositoryRoot = rootProject.projectDir.resolve("../../..").canonicalFile
val projectionProject = repositoryRoot.resolve("src/NetLift.Projection/NetLift.Projection.csproj")
val asyncManagedOldProject = repositoryRoot.resolve(
    "tests/NetLift.Negotiation.NativeHost.AsyncRuntime/Managed.Old/" +
        "NetLift.Negotiation.NativeHost.AsyncRuntime.Managed.Old.csproj"
)
val asyncManagedNewProject = repositoryRoot.resolve(
    "tests/NetLift.Negotiation.NativeHost.AsyncRuntime/Managed.New/" +
        "NetLift.Negotiation.NativeHost.AsyncRuntime.Managed.New.csproj"
)
val oldManagedHostAssemblyName =
    "NetLift.Negotiation.NativeHost.AsyncRuntime.Managed.Old"
val oldManagedBusinessAssemblyName = "Orders.AsyncBusiness"
val newManagedHostAssemblyName =
    "NetLift.Negotiation.NativeHost.AsyncRuntime.Managed.New"
val artifactsRootProvider = providers.gradleProperty("netliftArtifactsRoot")
val oldPublishOutput = layout.buildDirectory.dir("generated/async-v5/dotnet/old/publish")
val newPublishOutput = layout.buildDirectory.dir("generated/async-v5/dotnet/new/publish")
val oldGeneratedSource = layout.buildDirectory.file(
    "generated/async-v5/dotnet/old/generated/NetLift.Generator/" +
        "NetLift.Generator.LiftContractGenerator/LiftContract.g.cs"
)
val newGeneratedSource = layout.buildDirectory.file(
    "generated/async-v5/dotnet/new/generated/NetLift.Generator/" +
        "NetLift.Generator.LiftContractGenerator/LiftContract.g.cs"
)
val oldAssets = layout.buildDirectory.dir("generated/async-v5/android/old/assets")
val newAssets = layout.buildDirectory.dir("generated/async-v5/android/new/assets")
val oldKotlin = layout.buildDirectory.dir("generated/async-v5/android/old/kotlin")
val newKotlin = layout.buildDirectory.dir("generated/async-v5/android/new/kotlin")
val oldCodecOutput = layout.buildDirectory.dir("generated/async-v5/codecs/old-peer")
val newCodecOutput = layout.buildDirectory.dir("generated/async-v5/codecs/new-peer")

fun registerManagedPublish(
    taskName: String,
    projectFile: File,
    variant: String,
    output: Provider<org.gradle.api.file.Directory>
) = tasks.register<Exec>(taskName) {
    workingDir(repositoryRoot)
    doFirst {
        val root = artifactsRootProvider.orNull
            ?: throw GradleException(
                "Set -PnetliftArtifactsRoot to a unique session-artifacts directory."
            )
        val variantRoot = File(root, "async-$variant")
        val generated = layout.buildDirectory.dir(
            "generated/async-v5/dotnet/$variant/generated"
        ).get().asFile
        val publishDirectory = output.get().asFile
        if (publishDirectory.exists() && !publishDirectory.deleteRecursively())
            throw GradleException("Could not clear managed publish directory: $publishDirectory")
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
        environment("DOTNET_CLI_HOME", File(root, "dotnet-home").absolutePath)
        environment("DOTNET_NOLOGO", "true")
        environment("DOTNET_CLI_TELEMETRY_OPTOUT", "1")
    }
}

val publishOldManaged = registerManagedPublish(
    "publishAsyncV5OldManaged",
    asyncManagedOldProject,
    "old",
    oldPublishOutput
)
val publishNewManaged = registerManagedPublish(
    "publishAsyncV5NewManaged",
    asyncManagedNewProject,
    "new",
    newPublishOutput
)

val buildHostCodecProjection = tasks.register<Exec>("buildAsyncV5HostCodecProjection") {
    workingDir(repositoryRoot)
    doFirst {
        val root = artifactsRootProvider.orNull
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

val generateAsyncV5SampleInputs = tasks.register("generateAsyncV5SampleInputs") {
    dependsOn(publishOldManaged, publishNewManaged, buildHostCodecProjection)
    outputs.dirs(oldAssets, newAssets, oldKotlin, newKotlin, oldCodecOutput, newCodecOutput)
    outputs.upToDateWhen { false }

    doLast {
        listOf(oldAssets, newAssets, oldKotlin, newKotlin, oldCodecOutput, newCodecOutput)
            .forEach { it.get().asFile.deleteRecursively() }

        fun canonicalManifest(source: File): String {
            if (!source.isFile)
                throw GradleException("Generated managed contract was not found: $source")
            val literal = Regex(
                """public const string CanonicalJson = ("(?:\\.|[^"])*");"""
            ).find(source.readText())?.groupValues?.get(1)
                ?: throw GradleException("Generated Contract.CanonicalJson is missing in $source")
            val parsed = JsonSlurper().parseText(literal) as String
            if (!parsed.contains("\"profile\":\"async-callback-v1\""))
                throw GradleException("AsyncResult managed input is not an async-callback-v1 contract.")
            return parsed
        }

        fun validateGeneratedManagedHost(sourceFile: File, variant: String) {
            if (!sourceFile.isFile)
                throw GradleException("Generated managed host source was not found: $sourceFile")
            val source = sourceFile.readText()
            val requiredSurface = listOf(
                "public static class AsyncCallbackV1Host",
                "ILiftAsyncCallbackV1HostRegistry registry",
                "RegisterManagedRoute(17u, 1u",
                "DecodeRequest(",
                "EncodeResult(",
                "DispatchAsync(",
                "ILiftAsyncCallbackV1NativeServiceInvoker",
                "InvokeAsync(30u, 1u, request, invocationContext, cancellationToken)",
                "LiftNativeReplyDecoder.GetBooleanResult(reply, cancellationToken)"
            )
            val missing = requiredSurface.filterNot { source.contains(it) }
            if (missing.isNotEmpty()) {
                throw GradleException(
                    "Generated managed $variant host is missing the frozen " +
                        "AsyncCallbackV1 surface: ${missing.joinToString()}."
                )
            }
        }

        fun manifestHash(canonical: String): String {
            val root = JsonSlurper().parseText(canonical) as Map<*, *>
            val hash = root["schemaHash"] as? String
                ?: throw GradleException("Generated managed manifest has no schemaHash.")
            if (!hash.matches(Regex("[0-9a-fA-F]{64}")))
                throw GradleException("Generated managed schemaHash is not a SHA-256 value.")
            return hash.lowercase()
        }

        fun hasOptionalResultField(canonical: String): Boolean {
            val root = JsonSlurper().parseText(canonical) as Map<*, *>
            val schema = root["schema"] as? Map<*, *>
                ?: throw GradleException("Generated manifest has no schema.")
            val method = schema["method"] as? Map<*, *>
                ?: throw GradleException("Generated manifest has no method.")
            val resultDtoId = method["resultDtoId"] as? Number
                ?: throw GradleException("Generated manifest has no result DTO id.")
            val dtos = schema["dtos"] as? List<*>
                ?: throw GradleException("Generated async manifest has no DTO table.")
            val resultDto = dtos.mapNotNull { it as? Map<*, *> }
                .singleOrNull { (it["id"] as? Number)?.toLong() == resultDtoId.toLong() }
                ?: throw GradleException("Generated manifest has no unique result DTO.")
            val fields = resultDto["fields"] as? List<*>
                ?: throw GradleException("Generated result DTO has no fields.")
            return fields.any { (it as? Map<*, *>)?.get("optional") == true }
        }

        fun hasOptionalRequestField(canonical: String): Boolean {
            val root = JsonSlurper().parseText(canonical) as Map<*, *>
            val schema = root["schema"] as? Map<*, *>
                ?: throw GradleException("Generated manifest has no schema.")
            val method = schema["method"] as? Map<*, *>
                ?: throw GradleException("Generated manifest has no method.")
            val requestDtoId = method["requestDtoId"] as? Number
                ?: throw GradleException("Generated manifest has no request DTO id.")
            val dtos = schema["dtos"] as? List<*>
                ?: throw GradleException("Generated async manifest has no DTO table.")
            val requestDto = dtos.mapNotNull { it as? Map<*, *> }
                .singleOrNull { (it["id"] as? Number)?.toLong() == requestDtoId.toLong() }
                ?: throw GradleException("Generated manifest has no unique request DTO.")
            val fields = requestDto["fields"] as? List<*>
                ?: throw GradleException("Generated request DTO has no fields.")
            return fields.any { (it as? Map<*, *>)?.get("optional") == true }
        }

        fun stageHostCodec(
            peerContractSource: File,
            expectedPeerHash: String,
            codecOutput: File,
            targetKotlin: File
        ) {
            val root = artifactsRootProvider.orNull
                ?: throw GradleException(
                    "Set -PnetliftArtifactsRoot to a unique session-artifacts directory."
                )
            val projectionAssembly = File(
                root,
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
                    "--emit-schema-v5-async-host-codecs"
                )
            }

            val generated = File(codecOutput, "NetLift.kt")
            if (!generated.isFile)
                throw GradleException("Async host-bound Kotlin codec was not generated: $generated")
            val source = generated.readText()
            val generatedHash = Regex("schemaHash: String = \\\"([0-9a-f]{64})\\\"")
                .find(source)?.groupValues?.get(1)
            if (generatedHash != expectedPeerHash ||
                !source.contains("public object NetLiftV5AsyncCodec") ||
                !source.contains("public fun encodeRequest(") ||
                !source.contains("public fun decodeResult(") ||
                !source.contains("context: ManagedResultSchemaContext") ||
                !source.contains("context.requireActive()") ||
                source.contains("testingOnly")
            ) {
                throw GradleException(
                    "Generated async codec does not match the frozen peer-bound API."
                )
            }
            val target = File(targetKotlin, "com/netlift/androidhost/contract/NetLift.kt")
            target.parentFile.mkdirs()
            generated.copyTo(target, overwrite = true)
        }

        fun stageVariant(
            variant: String,
            packageName: String,
            assemblyName: String,
            localManifest: String,
            peerManifest: String,
            peerContractSource: File,
            managedHostSource: File,
            targetAssets: File,
            targetKotlin: File,
            codecOutput: File,
            publishDirectory: File,
            additionalRequiredAssemblies: Set<String>
        ) {
            val managedTarget = File(targetAssets, "netlift/apps/$packageName/dotnet")
            managedTarget.mkdirs()
            val assemblies = publishDirectory.listFiles()
                ?.filter { it.isFile && it.extension == "dll" }
                .orEmpty()
            val requiredAssemblies =
                setOf(assemblyName, "NetLift.Contracts", "NetLift.Negotiation") +
                    additionalRequiredAssemblies
            val missingAssemblies = requiredAssemblies.filterNot { requiredName ->
                assemblies.any { it.nameWithoutExtension == requiredName }
            }
            if (missingAssemblies.isNotEmpty()) {
                throw GradleException(
                    "Managed $variant publish output is missing required assemblies: " +
                        missingAssemblies.joinToString()
                )
            }
            assemblies.forEach { it.copyTo(File(managedTarget, it.name), overwrite = true) }

            validateGeneratedManagedHost(managedHostSource, variant)
            val managedHash = manifestHash(localManifest)
            val peerHash = manifestHash(peerManifest)
            val optionalResultField = hasOptionalResultField(localManifest)
            val peerOptionalResultField = hasOptionalResultField(peerManifest)
            val optionalRequestField = hasOptionalRequestField(peerManifest)
            stageHostCodec(peerContractSource, peerHash, codecOutput, targetKotlin)
            val hostConfig = mapOf(
                "managedAssemblyName" to assemblyName,
                "managedApiNamespace" to
                    "NetLift.Negotiation.NativeHost.Managed.${variant.replaceFirstChar { it.uppercase() }}",
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

            val identity = File(targetKotlin, "com/netlift/v5asyncsample/V5AsyncBuildIdentity.kt")
            identity.parentFile.mkdirs()
            identity.writeText(
                """
                package com.netlift.v5asyncsample

                internal object V5AsyncBuildIdentity {
                    const val expectedManagedSchemaHashHex = "$managedHash"
                    const val nativePeerSchemaHashHex = "$peerHash"
                    const val localResultHasOptionalField = $optionalResultField
                    const val peerRequestHasOptionalField = $optionalRequestField
                }
                """.trimIndent() + "\n"
            )

            val requestArguments = if (optionalRequestField) {
                "SubmitInput(name, count, \"compose\")"
            } else {
                "SubmitInput(name, count)"
            }
            val auditedExpression = if (peerOptionalResultField) "result.audited" else "null"
            val sampleCodec = File(targetKotlin, "com/netlift/v5asyncsample/V5AsyncSampleCodec.kt")
            sampleCodec.parentFile.mkdirs()
            sampleCodec.writeText(
                """
                package com.netlift.v5asyncsample

                import com.netlift.androidhost.contract.NetLiftV5AsyncCodec
                import com.netlift.androidhost.contract.SubmitInput
                import com.netlift.androidruntime.v5.ManagedResultSchemaContext
                import com.netlift.androidruntime.v5.NegotiatedPeerContext

                internal object V5AsyncSampleCodec {
                    fun encode(context: NegotiatedPeerContext, name: String, count: Int): ByteArray =
                        NetLiftV5AsyncCodec.encodeRequest($requestArguments, context)

                    fun decode(
                        source: ByteArray,
                        context: ManagedResultSchemaContext
                    ): V5AsyncSampleDecodedResult {
                        val result = NetLiftV5AsyncCodec.decodeResult(source, context)
                        return V5AsyncSampleDecodedResult(
                            result.code,
                            result.accepted,
                            $auditedExpression
                        )
                    }
                }
                """.trimIndent() + "\n"
            )
        }

        val oldManifest = canonicalManifest(oldGeneratedSource.get().asFile)
        val newManifest = canonicalManifest(newGeneratedSource.get().asFile)
        stageVariant(
            "old",
            "com.netlift.v5asyncsample.old",
            oldManagedHostAssemblyName,
            oldManifest,
            newManifest,
            newGeneratedSource.get().asFile,
            oldGeneratedSource.get().asFile,
            oldAssets.get().asFile,
            oldKotlin.get().asFile,
            newCodecOutput.get().asFile,
            oldPublishOutput.get().asFile,
            setOf(oldManagedBusinessAssemblyName)
        )
        stageVariant(
            "new",
            "com.netlift.v5asyncsample.new",
            newManagedHostAssemblyName,
            newManifest,
            oldManifest,
            oldGeneratedSource.get().asFile,
            newGeneratedSource.get().asFile,
            newAssets.get().asFile,
            newKotlin.get().asFile,
            oldCodecOutput.get().asFile,
            newPublishOutput.get().asFile,
            emptySet()
        )
    }
}

android {
    namespace = "com.netlift.v5asyncsample"
    compileSdk = 36

    defaultConfig {
        applicationId = "com.netlift.v5asyncsample"
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
            proguardFiles(
                getDefaultProguardFile("proguard-android-optimize.txt"),
                "proguard-rules.pro"
            )
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
    dependsOn(generateAsyncV5SampleInputs, ":runtime:publishReleasePublicationToSpikeLocalRepository")
}

val runtimeArtifactId =
    rootProject.extra["netliftAndroidRuntimeArtifactId"] as String

dependencies {
    implementation("com.netlift.android:$runtimeArtifactId:0.1.0-spike")
    implementation(platform("androidx.compose:compose-bom:2025.07.00"))
    implementation("androidx.activity:activity-compose:1.10.1")
    implementation("androidx.compose.material3:material3")
    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-android:1.10.2")
    testImplementation("junit:junit:4.13.2")
    debugImplementation("androidx.tracing:tracing:1.0.0")
    releaseImplementation("androidx.tracing:tracing:1.0.0")
    androidTestImplementation("androidx.tracing:tracing:1.0.0")
    androidTestImplementation("androidx.test:runner:1.6.2")
    androidTestImplementation("androidx.test.ext:junit:1.2.1")
}
