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

val generatedProjection = "../NetLift.Tests/bin/Debug/net11.0/projection-schema-v5-codecs/$projection"
val fixtureDirectory = "../NetLift.Tests/bin/Debug/net11.0/projection-schema-v5-codecs/fixtures"

kotlin {
    jvmToolchain(21)
    sourceSets.main {
        kotlin.srcDir("src/common/kotlin")
        kotlin.srcDir("src/$projection/kotlin")
        kotlin.srcDir(generatedProjection)
    }
}

application {
    mainClass.set(
        if (projection == "old")
            "com.netlift.androidhost.contract.SchemaV5OldCodecSmokeKt"
        else
            "com.netlift.androidhost.contract.SchemaV5NewCodecSmokeKt"
    )
}

tasks.named<JavaExec>("run") {
    args(fixtureDirectory)
}
