plugins {
    kotlin("jvm") version "2.2.20"
    application
}

repositories {
    mavenCentral()
}

val projection = providers.gradleProperty("netliftV5Projection").orElse("old").get()
require(projection == "old" || projection == "new") {
    "netliftV5Projection must be old or new."
}

val generatedRoot = providers.gradleProperty("netliftV5HostCodecRoot")
    .orElse("../NetLift.Tests/bin/Debug/net11.0/projection-schema-v5-host-codecs")
    .get()

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
        if (projection == "old")
            "com.netlift.androidhost.contract.SchemaV5OldHostCodecSmokeKt"
        else
            "com.netlift.androidhost.contract.SchemaV5NewHostCodecSmokeKt"
    )
}

tasks.named<JavaExec>("run") {
    args("$generatedRoot/fixtures")
}
