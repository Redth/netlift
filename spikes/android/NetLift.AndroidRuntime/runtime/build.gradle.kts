import org.gradle.api.DefaultTask
import org.gradle.api.GradleException
import org.gradle.api.provider.Property
import org.gradle.api.file.DirectoryProperty
import org.gradle.api.tasks.Input
import org.gradle.api.tasks.OutputDirectory
import org.gradle.api.tasks.Sync
import org.gradle.api.tasks.TaskAction
import org.gradle.api.publish.maven.MavenPublication
import java.util.zip.ZipFile

plugins {
    id("com.android.library")
    id("org.jetbrains.kotlin.android")
    id("maven-publish")
}

abstract class GenerateVariantSourceTask : DefaultTask() {
    @get:Input
    abstract val variantName: Property<String>

    @get:Input
    abstract val runtimeVersion: Property<String>

    @get:OutputDirectory
    abstract val outputDirectory: DirectoryProperty

    @TaskAction
    fun generate() {
        val target = outputDirectory.get().file(
            "com/netlift/androidruntime/generated/RuntimeVariant.kt"
        ).asFile
        target.parentFile.mkdirs()
        target.writeText(
            """
            package com.netlift.androidruntime.generated

            internal object RuntimeVariant {
                const val name = "${variantName.get()}"
                const val runtimeVersion = "${runtimeVersion.get()}"
            }
            """.trimIndent() + "\n"
        )
    }
}

group = "com.netlift.android"
version = "0.1.0-spike"

val useSourceV2Lifecycle =
    rootProject.extra["netliftAndroidUseSourceV2Lifecycle"] as Boolean
val runtimeArtifactId =
    rootProject.extra["netliftAndroidRuntimeArtifactId"] as String
val isolatedBuildRoot = providers.gradleProperty("netliftBuildRoot")
    .orNull
    ?.let { File(it).canonicalFile }
val cxxStagingDirectory = providers.gradleProperty("netliftCxxStagingDirectory")
    .orNull
    ?.let { File(it).canonicalFile }
    ?: isolatedBuildRoot?.resolve("cxx/runtime")
    ?: projectDir.resolve(".cxx")
if (useSourceV2Lifecycle &&
    cxxStagingDirectory.canonicalPath.startsWith(projectDir.canonicalPath + File.separator)) {
    throw GradleException(
        "Source-owned V2 mode requires a private -PnetliftBuildRoot or " +
            "-PnetliftCxxStagingDirectory outside the Android project."
    )
}

val dotnetRuntimeVersion = providers.gradleProperty("dotnetRuntimeVersion")
    .orElse("11.0.0-rc.1.26425.128")
    .get()
val runtimePack = File(
    System.getProperty("user.home"),
    ".nuget/packages/microsoft.netcore.app.runtime.android-arm64/$dotnetRuntimeVersion/runtimes/android-arm64"
)
val androidCryptoSupportJar =
    runtimePack.resolve("native/libSystem.Security.Cryptography.Native.Android.jar")
val generatedRuntimeAssets = layout.buildDirectory.dir("generated/runtimeAssets")
val generatedRuntimeJniLibs = layout.buildDirectory.dir("generated/runtimeJniLibs")
val managedHostOutput = layout.buildDirectory.dir("generated/managedHost")
val managedHostDebugOutput = layout.buildDirectory.dir("generated/managedHostDebug")
val generatedDebugManagedAssets = layout.buildDirectory.dir("generated/debugManagedAssets")
val managedHostDotnetArtifacts = layout.buildDirectory.dir("dotnetArtifacts")
val managedHostNugetPackages = layout.buildDirectory.dir("nugetPackages")

val buildManagedHost = tasks.register<Exec>("buildManagedHost") {
    workingDir(rootProject.projectDir)
    commandLine(
        "dotnet",
        "publish",
        "runtime/managed/NetLift.AndroidRuntime.Managed.csproj",
        "-c",
        "Release",
        "-o",
        managedHostOutput.get().asFile.absolutePath,
        "-p:ArtifactsPath=${managedHostDotnetArtifacts.get().dir("release").asFile.absolutePath}",
        "-p:RestorePackagesPath=${managedHostNugetPackages.get().dir("release").asFile.absolutePath}",
        "-v:minimal"
    )
}

val buildDebugManagedHost = tasks.register<Exec>("buildDebugManagedHost") {
    workingDir(rootProject.projectDir)
    commandLine(
        "dotnet",
        "publish",
        "runtime/managed/NetLift.AndroidRuntime.Managed.csproj",
        "-c",
        "Debug",
        "-o",
        managedHostDebugOutput.get().asFile.absolutePath,
        "-p:ArtifactsPath=${managedHostDotnetArtifacts.get().dir("debug").asFile.absolutePath}",
        "-p:RestorePackagesPath=${managedHostNugetPackages.get().dir("debug").asFile.absolutePath}",
        "-v:minimal"
    )
}

val stageDebugManagedRuntime = tasks.register<Sync>("stageDebugManagedRuntime") {
    dependsOn(buildDebugManagedHost)
    from(managedHostDebugOutput) {
        include("NetLift.AndroidRuntime.Managed.dll", "NetLift.Contracts.dll")
        into("netlift/runtime/$dotnetRuntimeVersion/dotnet")
    }
    into(generatedDebugManagedAssets)
}

val stageManagedRuntime = tasks.register<Sync>("stageManagedRuntime") {
    dependsOn(buildManagedHost)
    doFirst {
        val coreLib = runtimePack.resolve("lib/net11.0/System.Private.CoreLib.dll")
        if (!coreLib.isFile) {
            throw GradleException(
                ".NET $dotnetRuntimeVersion arm64 runtime pack is missing $coreLib"
            )
        }
    }
    from(runtimePack.resolve("lib/net11.0")) {
        include("*.dll")
        into("netlift/runtime/$dotnetRuntimeVersion/dotnet")
    }
    from(managedHostOutput) {
        include("NetLift.AndroidRuntime.Managed.dll", "NetLift.Contracts.dll")
        into("netlift/runtime/$dotnetRuntimeVersion/dotnet")
    }
    into(generatedRuntimeAssets)
}

val stageNativeRuntime = tasks.register<Sync>("stageNativeRuntime") {
    doFirst {
        val requiredLibraries = listOf("libcoreclr.so", "libclrjit.so")
        val missing = requiredLibraries.filterNot { runtimePack.resolve("native/$it").isFile }
        if (missing.isNotEmpty()) {
            throw GradleException(
                ".NET $dotnetRuntimeVersion arm64 runtime pack is missing native libraries: " +
                    missing.joinToString()
            )
        }
    }
    from(runtimePack.resolve("native")) {
        include("*.so")
        into("arm64-v8a")
    }
    into(generatedRuntimeJniLibs)
}

val verifyRuntimePack = tasks.register("verifyRuntimePack") {
    dependsOn(stageManagedRuntime, stageNativeRuntime)
    doLast {
        val managedRoot = generatedRuntimeAssets.get()
            .dir("netlift/runtime/$dotnetRuntimeVersion/dotnet").asFile
        val managedAssemblies = managedRoot.listFiles()
            ?.count { it.isFile && it.extension == "dll" } ?: 0
        val requiredManaged = listOf(
            "System.Private.CoreLib.dll",
            "NetLift.AndroidRuntime.Managed.dll",
            "NetLift.Contracts.dll"
        )
        val missingManaged = requiredManaged.filterNot { managedRoot.resolve(it).isFile }
        val forbiddenManaged = managedRoot.listFiles()
            ?.filter { it.name.startsWith("Orders") || it.name.contains("StubContract") }
            ?.map { it.name }
            .orEmpty()
        val nativeRoot = generatedRuntimeJniLibs.get().dir("arm64-v8a").asFile
        val requiredNative = listOf("libcoreclr.so", "libclrjit.so")
        val missingNative = requiredNative.filterNot { nativeRoot.resolve(it).isFile }
        if (managedAssemblies == 0 || missingManaged.isNotEmpty() ||
            !androidCryptoSupportJar.isFile ||
            forbiddenManaged.isNotEmpty() || missingNative.isNotEmpty()
        ) {
            throw GradleException(
                "Staged generic runtime pack is incomplete: managedAssemblies=$managedAssemblies, " +
                    "missingManaged=${missingManaged.joinToString()}, " +
                    "cryptoSupportJarPresent=${androidCryptoSupportJar.isFile}, " +
                    "forbiddenManaged=${forbiddenManaged.joinToString()}, " +
                    "missingNative=${missingNative.joinToString()}"
            )
        }
        logger.lifecycle(
            "Staged generic .NET $dotnetRuntimeVersion runtime: " +
                "$managedAssemblies managed DLLs, ${nativeRoot.listFiles()?.count { it.extension == "so" } ?: 0} arm64 libraries"
        )
    }
}

dependencies {
    implementation(files(androidCryptoSupportJar))
    testImplementation("junit:junit:4.13.2")
}

android {
    namespace = "com.netlift.androidruntime"
    compileSdk = 36
    ndkVersion = "28.2.13676358"

    defaultConfig {
        minSdk = 24
        ndk {
            abiFilters += "arm64-v8a"
        }
        buildConfigField(
            "String",
            "NETLIFT_DOTNET_RUNTIME_VERSION",
            "\"$dotnetRuntimeVersion\""
        )
        externalNativeBuild {
            cmake {
                cppFlags += "-std=c++17"
                arguments +=
                    "-DNETLIFT_ANDROID_SOURCE_V2_LIFECYCLE=${if (useSourceV2Lifecycle) "ON" else "OFF"}"
            }
        }
        consumerProguardFiles("consumer-rules.pro")
    }

    buildFeatures {
        buildConfig = true
    }

    buildTypes {
        getByName("debug") {
            externalNativeBuild {
                cmake {
                    cppFlags += "-DNETLIFT_ANDROID_RUNTIME_TEST_HOOKS"
                }
            }
        }
    }

    externalNativeBuild {
        cmake {
            path = file("src/main/cpp/CMakeLists.txt")
            version = "3.30.3"
            buildStagingDirectory = cxxStagingDirectory
        }
    }

    packaging {
        jniLibs {
            useLegacyPackaging = true
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    sourceSets {
        getByName("main") {
            assets.srcDir(generatedRuntimeAssets)
            jniLibs.srcDir(generatedRuntimeJniLibs)
        }
        getByName("debug") {
            assets.srcDir(generatedDebugManagedAssets)
        }
    }

    publishing {
        singleVariant("release") {
            withSourcesJar()
        }
    }

    kotlinOptions {
        jvmTarget = "17"
    }
}

androidComponents.onVariants { variant ->
    val capitalizedName = variant.name.replaceFirstChar { it.uppercase() }
    val generateSource = tasks.register<GenerateVariantSourceTask>(
        "generate${capitalizedName}RuntimeVariantSource"
    ) {
        variantName.set(variant.name)
        runtimeVersion.set(dotnetRuntimeVersion)
        outputDirectory.set(
            layout.buildDirectory.dir("generated/sourceProbe/${variant.name}")
        )
    }

    val kotlinSourceSet = kotlin.sourceSets.findByName(variant.name)
        ?: throw GradleException("Kotlin source set ${variant.name} is unavailable.")
    kotlinSourceSet.kotlin.srcDir(generateSource.flatMap { it.outputDirectory })
}

tasks.named("preBuild").configure {
    dependsOn(verifyRuntimePack)
}

tasks.matching { it.name == "mergeDebugAssets" }.configureEach {
    dependsOn(stageDebugManagedRuntime)
}

val verifyGenericRuntimeAar = tasks.register("verifyGenericRuntimeAar") {
    dependsOn("bundleReleaseAar")
    doLast {
        val aar = layout.buildDirectory.file("outputs/aar/runtime-release.aar").get().asFile
        if (!aar.isFile)
            throw GradleException("Generic runtime AAR was not created at ${aar.absolutePath}.")

        val nativeCpp = projectDir.resolve("src/main/cpp/netlift_androidruntime.cpp").readText()
        val privateCxxOutput = layout.buildDirectory
            .dir("intermediates/cxx")
            .get()
            .asFile
            .absolutePath
        val selectedCmakeCaches = project.fileTree(cxxStagingDirectory)
            .matching { include("**/CMakeCache.txt") }
            .files
            .filter { cache ->
                cache.readLines().any { line ->
                    line.startsWith(
                        "CMAKE_LIBRARY_OUTPUT_DIRECTORY:UNINITIALIZED=$privateCxxOutput/"
                    )
                }
            }
        val expectedOption = "NETLIFT_ANDROID_SOURCE_V2_LIFECYCLE:BOOL=" +
            if (useSourceV2Lifecycle) "ON" else "OFF"
        val wrongModeCaches = selectedCmakeCaches.filterNot { cache ->
            cache.readText().contains(expectedOption)
        }
        val nativeBuildFiles = selectedCmakeCaches.map { cache ->
            cache.resolveSibling("build.ninja")
        }.filter { it.isFile }
        val missingCommonNativeSources = listOf(
            "netlift_androidruntime.cpp.o",
            "netlift_android_v2_provider.c.o",
            "async_v2_core.c.o"
        ).filterNot { source ->
            nativeBuildFiles.isNotEmpty() &&
                nativeBuildFiles.all { it.readText().contains(source) }
        }
        val requiredLifecycleSources = if (useSourceV2Lifecycle) {
            listOf("async_v2_lifecycle.c.o", "async_v2_accessor.c.o")
        } else {
            listOf("CallbackLifecycleV2State.c.o", "CallbackLifecycleV2Accessor.c.o")
        }
        val forbiddenLifecycleSources = if (useSourceV2Lifecycle) {
            listOf("CallbackLifecycleV2State.c.o", "CallbackLifecycleV2Accessor.c.o")
        } else {
            listOf("async_v2_lifecycle.c.o", "async_v2_accessor.c.o")
        }
        val missingLifecycleSources = requiredLifecycleSources.filterNot { source ->
            nativeBuildFiles.isNotEmpty() &&
                nativeBuildFiles.all { it.readText().contains(source) }
        }
        val mixedLifecycleSources = forbiddenLifecycleSources.filter { source ->
            nativeBuildFiles.any { it.readText().contains(source) }
        }
        val forbiddenNativeSources = listOf(
            "runtime_v2_adapter.c",
            "test_v2_provider.c"
        ).filter { source ->
            nativeBuildFiles.any { it.readText().contains(source) }
        }
        val forbiddenLegacyAdapterIncludes = listOf("runtime_v2_adapter.h")
            .filter(nativeCpp::contains)
        val expectedArtifactId = if (useSourceV2Lifecycle) {
            "netlift-android-runtime-source-v2-test"
        } else {
            "netlift-android-runtime"
        }
        val artifactIdentityMismatch = runtimeArtifactId != expectedArtifactId
        val providerSource = projectDir
            .resolve("src/main/cpp/netlift_android_v2_provider.c")
            .readText()
        val cmakeSource = projectDir.resolve("src/main/cpp/CMakeLists.txt").readText()
        val missingTestOnlyProviderGuard =
            !providerSource.contains("The Android V2 provider is an experimental test-only host.")
        val missingTestOnlyCompileDefine =
            !cmakeSource.contains("NETLIFT_ANDROID_ASYNC_V2_TEST_ONLY=1")
        val missingBuildEvidence =
            selectedCmakeCaches.isEmpty() || nativeBuildFiles.isEmpty()

        val entries = ZipFile(aar).use { archive ->
            archive.entries().asSequence().map { it.name }.toList()
        }
        val requiredEntries = listOf(
            "jni/arm64-v8a/libnetlift_androidruntime.so",
            "assets/netlift/runtime/$dotnetRuntimeVersion/dotnet/NetLift.AndroidRuntime.Managed.dll",
            "assets/netlift/runtime/$dotnetRuntimeVersion/dotnet/NetLift.Contracts.dll"
        )
        val missingEntries = requiredEntries.filterNot(entries::contains)
        val forbiddenEntries = entries.filter {
            it.contains("Orders") ||
                it.endsWith("/Orders.kt") ||
                it.contains("StubContract") ||
                it.startsWith("assets/netlift/apps/") ||
                it.endsWith("/contract.json") ||
                it.endsWith("/NetLift.kt") ||
                it.endsWith("/netlift.h") ||
                it.contains("runtime_v2_adapter") ||
                it.endsWith(".c") ||
                it.endsWith(".cc") ||
                it.endsWith(".cpp") ||
                it.endsWith(".h")
        }
        if (missingBuildEvidence || wrongModeCaches.isNotEmpty() ||
            missingCommonNativeSources.isNotEmpty() || missingLifecycleSources.isNotEmpty() ||
            mixedLifecycleSources.isNotEmpty() || forbiddenNativeSources.isNotEmpty() ||
            forbiddenLegacyAdapterIncludes.isNotEmpty() ||
            missingEntries.isNotEmpty() || forbiddenEntries.isNotEmpty() ||
            artifactIdentityMismatch || missingTestOnlyProviderGuard ||
            missingTestOnlyCompileDefine) {
            throw GradleException(
                "Generic runtime AAR or selected CMake source set is invalid: " +
                    "missingBuildEvidence=$missingBuildEvidence, " +
                    "wrongModeCaches=${wrongModeCaches.joinToString()}, " +
                    "missingCommonNativeSources=${missingCommonNativeSources.joinToString()}, " +
                    "missingLifecycleSources=${missingLifecycleSources.joinToString()}, " +
                    "mixedLifecycleSources=${mixedLifecycleSources.joinToString()}, " +
                    "forbiddenNativeSources=${forbiddenNativeSources.joinToString()}, " +
                    "forbiddenLegacyAdapterIncludes=${forbiddenLegacyAdapterIncludes.joinToString()}, " +
                    "artifactIdentityMismatch=$artifactIdentityMismatch, " +
                    "missingTestOnlyProviderGuard=$missingTestOnlyProviderGuard, " +
                    "missingTestOnlyCompileDefine=$missingTestOnlyCompileDefine, " +
                    "missing=${missingEntries.joinToString()}, " +
                    "contractOrSourceLeaks=${forbiddenEntries.joinToString()}"
            )
        }
        logger.lifecycle(
            "Verified test-only Android runtime AAR " +
                "com.netlift.android:$runtimeArtifactId:$version at $aar " +
                "(${entries.count { it.endsWith(".dll") }} managed DLLs, " +
                "${entries.count { it.endsWith(".so") }} native libraries; " +
                "V2 lifecycle mode=${if (useSourceV2Lifecycle) "source-owned test spike" else "legacy test spike"}, " +
                "one selected source set, test-only Android provider present, " +
                "no fixture adapter source or app contract files)"
        )
    }
}

tasks.matching { it.name == "publishReleasePublicationToSpikeLocalRepository" }
    .configureEach {
        dependsOn(verifyGenericRuntimeAar)
    }

afterEvaluate {
    publishing {
        publications {
            create<MavenPublication>("release") {
                from(components["release"])
                groupId = "com.netlift.android"
                artifactId = runtimeArtifactId
                version = "0.1.0-spike"
            }
        }
        repositories {
            maven {
                name = "spikeLocal"
                url = uri(layout.buildDirectory.dir("local-maven"))
            }
        }
    }
}
