import org.gradle.api.tasks.Exec
import org.gradle.api.tasks.Sync
import org.gradle.api.GradleException

plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
    id("org.jetbrains.kotlin.plugin.compose")
}

val ordersContractPublish = layout.buildDirectory.dir("generated/ordersContractPublish")
val generatedOrdersContractAssets = layout.buildDirectory.dir("generated/ordersContractAssets")
val generatedOrdersProjection = layout.buildDirectory.dir("generated/ordersProjection")
val generatedOrdersKotlin = layout.buildDirectory.dir("generated/ordersKotlin")
val generatedOrdersManifest = layout.buildDirectory.dir("generated/ordersManifest")
val ordersContractProject = file("managed-orders/NetLift.AndroidRuntime.OrdersContract.csproj")
val ordersContractGeneratedSource = file(
    "managed-orders/obj/generated/NetLift.Generator/NetLift.Generator.LiftContractGenerator/LiftContract.g.cs"
)
val ordersHostModuleGeneratedSource = file(
    "managed-orders/obj/generated/NetLift.Generator/NetLift.Generator.LiftContractGenerator/LiftHostModule.g.cs"
)
val ordersProjectionHeader = generatedOrdersProjection.get().file("netlift.h").asFile
val ordersContractManifest = generatedOrdersManifest.get()
    .file("netlift/apps/com.netlift.sample/contract.json").asFile

val publishOrdersContract = tasks.register<Exec>("publishOrdersContract") {
    workingDir(rootProject.projectDir)
    commandLine(
        "dotnet",
        "publish",
        "sample/managed-orders/NetLift.AndroidRuntime.OrdersContract.csproj",
        "-c",
        "Release",
        "-o",
        ordersContractPublish.get().asFile.absolutePath,
        "-v:minimal"
    )
}
val projectOrdersContract = tasks.register<Exec>("projectOrdersContract") {
    dependsOn(publishOrdersContract)
    workingDir(rootProject.projectDir.resolve("../../.."))
    commandLine(
        "dotnet",
        "run",
        "--project",
        "src/NetLift.Projection/NetLift.Projection.csproj",
        "-c",
        "Release",
        "--",
        ordersContractGeneratedSource.absolutePath,
        generatedOrdersProjection.get().asFile.absolutePath,
        "--drop-late-callback-replies-after-release",
        "--kotlin-callback-lifecycle-v2"
    )
}
val stageOrdersProjection = tasks.register<Sync>("stageOrdersProjection") {
    dependsOn(projectOrdersContract)
    from(generatedOrdersProjection) {
        include("NetLift.kt")
    }
    into(generatedOrdersKotlin)
}
val generateOrdersContractManifest = tasks.register("generateOrdersContractManifest") {
    dependsOn(projectOrdersContract)
    outputs.file(ordersContractManifest)
    doLast {
        val header = ordersProjectionHeader.readText()
        val hostModule = ordersHostModuleGeneratedSource.readText()
        fun macro(name: String): String =
            Regex("(?m)^#define\\s+$name\\s+(\\S+)").find(header)?.groupValues?.get(1)
                ?: throw GradleException("Generated typed contract is missing $name.")
        fun feature(name: String): Int = macro(name).removeSuffix("u").removePrefix("0x").let {
            it.toInt(if (macro(name).startsWith("0x")) 16 else 10)
        }
        val hash = macro("LIFT_SCHEMA_HASH_HEX").trim('"')
        val requiredFeatureBits = Regex("RequiredFeatureBits = (\\d+)u;")
            .find(hostModule)?.groupValues?.get(1)
            ?: throw GradleException("Generated managed host module has no required feature bits.")
        val moduleFeatureBits = requiredFeatureBits.toInt()
        val ordersServiceId = macro("LIFT_SERVICE_ID_2")
        val placeOrderMethodId = macro("LIFT_METHOD_ID_2_1")
        val confirmationServiceId = macro("LIFT_NATIVE_SERVICE_ID_3")
        val confirmationMethodId = macro("LIFT_METHOD_ID_3_1")
        val manifest = """
            {
              "assemblyName": "NetLift.AndroidRuntime.OrdersContract",
              "abiVersion": ${macro("LIFT_ABI_VERSION").removeSuffix("u")},
              "requiredFeatureBits": $requiredFeatureBits,
              "schemaHash": "$hash",
              "ordersServiceId": $ordersServiceId,
              "placeOrderMethodId": $placeOrderMethodId,
              "confirmationServiceId": $confirmationServiceId,
              "confirmationMethodId": $confirmationMethodId,
              "managedAssetDirectory": "netlift/apps/com.netlift.sample/dotnet"
            }
        """.trimIndent() + "\n"
        check(feature("LIFT_FEATURE_ASYNC_CALLBACKS") == 2 &&
            feature("LIFT_FEATURE_TYPED_NATIVE_REPLY") == 4 &&
            feature("LIFT_FEATURE_STREAMS") == 8 &&
            moduleFeatureBits == 15) {
            "Generated typed-v4 feature constants do not match the expected protocol."
        }
        ordersContractManifest.parentFile.mkdirs()
        ordersContractManifest.writeText(manifest)
    }
}
val stageOrdersContractAssets = tasks.register<Sync>("stageOrdersContractAssets") {
    dependsOn(publishOrdersContract)
    from(ordersContractPublish) {
        include(
            "NetLift.AndroidRuntime.OrdersContract.dll",
            "Orders.Business.dll",
            "NetLift.AndroidRuntime.OrdersContract.deps.json",
            "NetLift.AndroidRuntime.OrdersContract.runtimeconfig.json"
        )
        into("netlift/apps/com.netlift.sample/dotnet")
    }
    into(generatedOrdersContractAssets)
}

android {
    namespace = "com.netlift.sample"
    compileSdk = 36

    defaultConfig {
        applicationId = "com.netlift.sample"
        minSdk = 24
        targetSdk = 36
        versionCode = 1
        versionName = "1.0"
        ndk {
            abiFilters += "arm64-v8a"
        }
        testInstrumentationRunner = "androidx.test.runner.AndroidJUnitRunner"
    }

    buildFeatures {
        compose = true
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
        getByName("main") {
            assets.srcDir(generatedOrdersContractAssets)
            assets.srcDir(generatedOrdersManifest)
        }
    }

    packaging {
        jniLibs {
            useLegacyPackaging = true
        }
    }
}

kotlin.sourceSets.getByName("main").kotlin.srcDir(generatedOrdersKotlin)

tasks.named("preBuild").configure {
    dependsOn(stageOrdersContractAssets, stageOrdersProjection, generateOrdersContractManifest)
}

val runtimeArtifactId =
    rootProject.extra["netliftAndroidRuntimeArtifactId"] as String

dependencies {
    debugImplementation(project(":runtime"))
    releaseImplementation("com.netlift.android:$runtimeArtifactId:0.1.0-spike")
    implementation(platform("androidx.compose:compose-bom:2025.07.00"))
    implementation("androidx.activity:activity-compose:1.10.1")
    implementation("androidx.compose.material3:material3")
    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-core:1.10.2")
    androidTestImplementation("androidx.test:runner:1.6.2")
    androidTestImplementation("androidx.test.ext:junit:1.2.1")
}
