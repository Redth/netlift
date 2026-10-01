plugins {
    kotlin("jvm") version "2.2.20"
    application
}

repositories {
    mavenCentral()
}

val projection = providers.gradleProperty("netliftAsyncV5Projection").orElse("old").get()
require(projection == "old" || projection == "new" || projection == "utf8") {
    "netliftAsyncV5Projection must be old, new, or utf8."
}

val generatedRoot = providers.gradleProperty("netliftAsyncV5HostCodecRoot")
    .orElse("../NetLift.Tests/bin/Debug/net11.0/projection-schema-v5-async-host-codecs")
    .get()
providers.gradleProperty("netliftAsyncV5GradleBuildDir").orNull?.let {
    layout.buildDirectory.set(file(it))
}

kotlin {
    jvmToolchain(21)
    sourceSets.main {
        kotlin.srcDir("src/common/kotlin")
        kotlin.srcDir("src/$projection/kotlin")
        kotlin.srcDir("$generatedRoot/$projection")
    }
}

application {
    mainClass.set(
        when (projection) {
            "old" -> "com.netlift.androidhost.contract.AsyncV5OldHostCodecSmokeKt"
            "new" -> "com.netlift.androidhost.contract.AsyncV5NewHostCodecSmokeKt"
            else -> "com.netlift.androidhost.contract.AsyncV5Utf8HostCodecSmokeKt"
        }
    )
}

tasks.named<JavaExec>("run") {
    args("$generatedRoot/fixtures")
}
