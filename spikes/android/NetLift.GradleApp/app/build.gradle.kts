import org.gradle.api.tasks.Exec
import org.gradle.api.tasks.Sync

plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
    id("org.jetbrains.kotlin.plugin.compose")
}

val dotnetRuntimeVersion = providers.gradleProperty("dotnetRuntimeVersion")
    .orElse("11.0.0-rc.1.26425.128")
    .get()
val stressDiagnosticsEnabled = providers.gradleProperty("netliftStressDiagnostics")
    .map { it.toBoolean() }
    .getOrElse(false)
val runtimePack = File(
    System.getProperty("user.home"),
    ".nuget/packages/microsoft.netcore.app.runtime.android-arm64/$dotnetRuntimeVersion/runtimes/android-arm64"
)
val managedOutput = rootProject.file("../NetLift.Managed/bin/Release/net11.0/android-arm64")
val generatedDotnetAssets = layout.buildDirectory.dir("generated/dotnetAssets")
val generatedNativeLibs = layout.buildDirectory.dir("generated/dotnetJniLibs")
val projectedContract = rootProject.file("../../../tests/NetLift.NativeHost.Managed/obj/projected")

val buildManaged = tasks.register<Exec>("buildManagedCore") {
    workingDir(rootProject.file(".."))
    commandLine(
        "dotnet",
        "build",
        "NetLift.Managed/NetLift.Managed.csproj",
        "-c",
        "Release",
        "-v:minimal"
    )
}

val validateCoreClrInputs = tasks.register("validateCoreClrInputs") {
    dependsOn(buildManaged)
    doLast {
        val requiredFiles = listOf(
            runtimePack.resolve("lib/net11.0/System.Private.CoreLib.dll"),
            runtimePack.resolve("native/libcoreclr.so"),
            runtimePack.resolve("native/libclrjit.so"),
            File(managedOutput, "NetLift.Managed.dll"),
            File(managedOutput, "Orders.Business.dll")
        )
        val missing = requiredFiles.filterNot(File::isFile)
        if (!runtimePack.isDirectory || missing.isNotEmpty()) {
            val missingPaths = missing.joinToString("\n") { "  - ${it.absolutePath}" }
            throw GradleException(
                "The .NET $dotnetRuntimeVersion Android arm64 runtime inputs are incomplete.\n" +
                    "Runtime pack directory: ${runtimePack.absolutePath}\n" +
                    (if (missingPaths.isEmpty()) "" else "Missing files:\n$missingPaths\n") +
                    "Restore/build NetLift.Managed with the pinned .NET SDK and RID android-arm64, " +
                    "and ensure Microsoft.NETCore.App.Runtime.android-arm64 $dotnetRuntimeVersion " +
                    "is available in the NuGet package cache before assembling the APK."
            )
        }
    }
}

val stageDotnetAssets = tasks.register<Sync>("stageDotnetAssets") {
    dependsOn(validateCoreClrInputs)
    from(runtimePack.resolve("lib/net11.0")) {
        include("*.dll")
        into("dotnet")
    }
    from(managedOutput) {
        include("NetLift.Managed.dll", "Orders.Business.dll")
        into("dotnet")
    }
    into(generatedDotnetAssets)
}

val stageCoreClrNativeLibs = tasks.register<Sync>("stageCoreClrNativeLibs") {
    dependsOn(validateCoreClrInputs)
    from(runtimePack.resolve("native")) {
        include("*.so")
        into("arm64-v8a")
    }
    into(generatedNativeLibs)
}

val verifyStagedCoreClrAssets = tasks.register("verifyStagedCoreClrAssets") {
    dependsOn(stageDotnetAssets, stageCoreClrNativeLibs)
    doLast {
        val assetRoot = generatedDotnetAssets.get().asFile
        val stagedRuntime = File(assetRoot, "dotnet")
        val managedDlls = assetRoot.walkTopDown()
            .filter { it.isFile && it.extension == "dll" }
            .toList()
        val misplacedDlls = managedDlls.filter { it.parentFile != stagedRuntime }
        val duplicateDllNames = managedDlls
            .groupBy { it.name }
            .filterValues { it.size > 1 }
            .keys
        val requiredManaged = listOf(
            File(stagedRuntime, "System.Private.CoreLib.dll"),
            File(stagedRuntime, "NetLift.Managed.dll"),
            File(stagedRuntime, "Orders.Business.dll")
        )
        val missingManaged = requiredManaged.filterNot(File::isFile)
        val stagedNative = File(generatedNativeLibs.get().asFile, "arm64-v8a")
        val requiredNative = listOf(
            File(stagedNative, "libcoreclr.so"),
            File(stagedNative, "libclrjit.so")
        )
        val missingNative = requiredNative.filterNot(File::isFile)
        if (misplacedDlls.isNotEmpty() || duplicateDllNames.isNotEmpty() ||
            missingManaged.isNotEmpty() || missingNative.isNotEmpty()
        ) {
            val details = buildString {
                if (misplacedDlls.isNotEmpty()) {
                    appendLine("Managed DLLs found outside assets/dotnet:")
                    misplacedDlls.forEach { appendLine("  - ${it.absolutePath}") }
                }
                if (duplicateDllNames.isNotEmpty()) {
                    appendLine("Duplicate managed assembly names:")
                    duplicateDllNames.forEach { appendLine("  - $it") }
                }
                (missingManaged + missingNative).forEach { appendLine("Missing staged runtime input: ${it.absolutePath}") }
            }
            throw GradleException("Staged CoreCLR assets are invalid.\n$details")
        }
    }
}

android {
    namespace = "com.netlift.androidhost"
    compileSdk = 36
    ndkVersion = "28.2.13676358"

    defaultConfig {
        applicationId = "com.netlift.androidhost"
        minSdk = 24
        targetSdk = 36
        versionCode = 1
        versionName = "1.0"
        buildConfigField("boolean", "NETLIFT_STRESS_DIAGNOSTICS", "false")

        ndk {
            abiFilters += "arm64-v8a"
        }

        externalNativeBuild {
            cmake {
                cppFlags += "-std=c++17"
            }
        }
    }

    buildTypes {
        getByName("release") {
            isMinifyEnabled = false
        }
        create("minifiedRelease") {
            initWith(getByName("release"))
            applicationIdSuffix = ".minified"
            isMinifyEnabled = true
            buildConfigField(
                "boolean",
                "NETLIFT_STRESS_DIAGNOSTICS",
                stressDiagnosticsEnabled.toString()
            )
            proguardFiles(
                getDefaultProguardFile("proguard-android-optimize.txt"),
                "proguard-rules.pro"
            )
            matchingFallbacks += listOf("release")
        }
    }

    sourceSets {
        getByName("main") {
            assets.srcDir(generatedDotnetAssets)
            jniLibs.srcDir(generatedNativeLibs)
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

    externalNativeBuild {
        cmake {
            path = file("src/main/cpp/CMakeLists.txt")
            version = "3.30.3"
        }
    }

    packaging {
        jniLibs {
            useLegacyPackaging = true
        }
    }
}

kotlin.sourceSets.getByName("main").kotlin.srcDir(projectedContract)

val verifyProjectedContract = tasks.register("verifyProjectedContract") {
    doLast {
        val required = listOf(
            File(projectedContract, "NetLift.kt"),
            File(projectedContract, "netlift.h")
        )
        val missing = required.filterNot(File::isFile)
        if (missing.isNotEmpty()) {
            throw GradleException(
                "Generated NetLift Kotlin/C projections are missing:\n" +
                    missing.joinToString("\n") { "  - ${it.absolutePath}" } +
                    "\nRun the NetLift NativeHost.Managed build and projection step before assembling Android."
            )
        }
    }
}

tasks.named("preBuild").configure {
    dependsOn(verifyStagedCoreClrAssets, verifyProjectedContract)
}

dependencies {
    implementation(platform("androidx.compose:compose-bom:2025.07.00"))
    implementation("androidx.activity:activity-compose:1.10.1")
    implementation("androidx.compose.material3:material3")
    implementation("androidx.compose.ui:ui")
    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-core:1.10.2")
}
