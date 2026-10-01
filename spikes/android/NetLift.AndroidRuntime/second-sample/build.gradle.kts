import org.gradle.api.GradleException
import org.gradle.api.tasks.Exec
import org.gradle.api.tasks.Sync

plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
    id("org.jetbrains.kotlin.plugin.compose")
}

val contractPublish = layout.buildDirectory.dir("generated/contractPublish")
val contractAssets = layout.buildDirectory.dir("generated/contractAssets")
val projection = layout.buildDirectory.dir("generated/projection")
val kotlinProjection = layout.buildDirectory.dir("generated/kotlin")
val contractManifest = layout.buildDirectory.dir("generated/manifest")
    .get().file("netlift/apps/com.netlift.secondcontract/contract.json").asFile
val generatedContractSource = file(
    "managed/obj/generated/NetLift.Generator/NetLift.Generator.LiftContractGenerator/LiftContract.g.cs"
)
val generatedHostSource = file(
    "managed/obj/generated/NetLift.Generator/NetLift.Generator.LiftContractGenerator/LiftHostModule.g.cs"
)
val projectionHeader = projection.get().file("netlift.h").asFile

val publishContract = tasks.register<Exec>("publishSecondContract") {
    workingDir(rootProject.projectDir)
    commandLine(
        "dotnet",
        "publish",
        "second-sample/managed/NetLift.AndroidRuntime.SecondContract.csproj",
        "-c",
        "Release",
        "-o",
        contractPublish.get().asFile.absolutePath,
        "-v:minimal"
    )
}

val generateProjection = tasks.register<Exec>("projectSecondContract") {
    dependsOn(publishContract)
    workingDir(rootProject.projectDir.resolve("../../.."))
    commandLine(
        "dotnet",
        "run",
        "--project",
        "src/NetLift.Projection/NetLift.Projection.csproj",
        "-c",
        "Release",
        "--",
        generatedContractSource.absolutePath,
        projection.get().asFile.absolutePath,
        "--drop-late-callback-replies-after-release",
        "--emit-kotlin-flat-codecs",
        "--kotlin-generated-service-facade-v2"
    )
}

val stageProjection = tasks.register<Sync>("stageSecondProjection") {
    dependsOn(generateProjection)
    from(projection) {
        include("NetLift.kt")
    }
    into(kotlinProjection)
}

val generateManifest = tasks.register("generateSecondContractManifest") {
    dependsOn(generateProjection)
    outputs.file(contractManifest)
    doLast {
        val header = projectionHeader.readText()
        val hostModule = generatedHostSource.readText()
        fun macro(name: String): String =
            Regex("(?m)^#define\\s+$name\\s+(\\S+)").find(header)?.groupValues?.get(1)
                ?: throw GradleException("Generated contract is missing $name.")
        fun feature(name: String): Int {
            val value = macro(name)
            val digits = value.removeSuffix("u").removePrefix("0x")
            return digits.toInt(if (value.startsWith("0x")) 16 else 10)
        }

        val requiredFeatureBits = Regex("RequiredFeatureBits = (\\d+)u;")
            .find(hostModule)?.groupValues?.get(1)
            ?: throw GradleException("Generated host module has no required feature bits.")
        check(feature("LIFT_FEATURE_ASYNC_CALLBACKS") == 2 &&
            feature("LIFT_FEATURE_TYPED_NATIVE_REPLY") == 4 &&
            requiredFeatureBits.toInt() == 6) {
            "The second app must use only async callbacks and typed native replies."
        }
        val manifest = """
            {
              "assemblyName": "NetLift.AndroidRuntime.SecondContract",
              "abiVersion": ${macro("LIFT_ABI_VERSION").removeSuffix("u")},
              "requiredFeatureBits": $requiredFeatureBits,
              "schemaHash": "${macro("LIFT_SCHEMA_HASH_HEX").trim('"')}",
              "ordersServiceId": ${macro("LIFT_SERVICE_ID_41")},
              "placeOrderMethodId": ${macro("LIFT_METHOD_ID_41_1")},
              "confirmationServiceId": ${macro("LIFT_NATIVE_SERVICE_ID_42")},
              "confirmationMethodId": ${macro("LIFT_METHOD_ID_42_1")},
              "managedAssetDirectory": "netlift/apps/com.netlift.secondcontract/dotnet"
            }
        """.trimIndent() + "\n"
        contractManifest.parentFile.mkdirs()
        contractManifest.writeText(manifest)
    }
}

val stageContractAssets = tasks.register<Sync>("stageSecondContractAssets") {
    dependsOn(publishContract)
    from(contractPublish) {
        include(
            "NetLift.AndroidRuntime.SecondContract.dll",
            "NetLift.AndroidRuntime.SecondContract.deps.json",
            "NetLift.AndroidRuntime.SecondContract.runtimeconfig.json"
        )
        into("netlift/apps/com.netlift.secondcontract/dotnet")
    }
    into(contractAssets)
}

android {
    namespace = "com.netlift.secondcontract"
    compileSdk = 36

    defaultConfig {
        applicationId = "com.netlift.secondcontract"
        minSdk = 24
        targetSdk = 36
        versionCode = 1
        versionName = "1.0"
        testInstrumentationRunner = "androidx.test.runner.AndroidJUnitRunner"
        ndk {
            abiFilters += "arm64-v8a"
        }
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
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"))
        }
    }

    sourceSets {
        getByName("main") {
            assets.srcDir(contractAssets)
            assets.srcDir(contractManifest.parentFile.parentFile.parentFile.parentFile)
        }
    }

    packaging {
        jniLibs {
            useLegacyPackaging = true
        }
    }
}

kotlin.sourceSets.getByName("main").kotlin.srcDir(kotlinProjection)

tasks.named("preBuild").configure {
    dependsOn(stageContractAssets, stageProjection, generateManifest)
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
    testImplementation("junit:junit:4.13.2")
    androidTestImplementation("androidx.test:runner:1.6.2")
    androidTestImplementation("androidx.test.ext:junit:1.2.1")
}
