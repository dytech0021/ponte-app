import java.util.Properties

plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

// Arquivos locais, fora do git: o servidor de retransmissão com a chave, e a chave que assina o APK.
fun localProperties(name: String) = Properties().apply {
    val file = rootProject.file(name)
    if (file.exists()) file.inputStream().use { load(it) }
}

val relay = localProperties("relay.properties")
val keystore = localProperties("keystore.properties")

android {
    namespace = "com.dytech.ponte"
    compileSdk = 35

    defaultConfig {
        applicationId = "com.dytech.ponte"
        minSdk = 26
        targetSdk = 35
        // A cada versão publicada: versionCode sobe de um em um e versionName acompanha.
        versionCode = 14
        versionName = "1.8.4"
        buildConfigField("String", "RELAY_SERVER", "\"${relay.getProperty("server", "")}\"")
        buildConfigField("String", "RELAY_KEY", "\"${relay.getProperty("key", "")}\"")
        // Os links das transmissões de tela (https://servidor/ponte/assistir/#código) abrem no aplicativo: o servidor
        // é o mesmo da retransmissão, pelo nome.
        manifestPlaceholders["webHost"] = relay.getProperty("server", "").substringBefore(':').ifEmpty { "filabox3d.com.br" }
    }

    signingConfigs {
        if (keystore.getProperty("storeFile") != null) {
            create("release") {
                storeFile = rootProject.file(keystore.getProperty("storeFile"))
                storePassword = keystore.getProperty("storePassword")
                keyAlias = keystore.getProperty("keyAlias")
                keyPassword = keystore.getProperty("keyPassword")
            }
        }
    }

    buildTypes {
        release {
            isMinifyEnabled = false
            signingConfig = signingConfigs.findByName("release")
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_11
        targetCompatibility = JavaVersion.VERSION_11
    }
    kotlinOptions {
        jvmTarget = "11"
    }
    buildFeatures {
        buildConfig = true
    }
    testOptions {
        unitTests {
            // Os testes abrem as telas de verdade num Android simulado (Robolectric), com os recursos do aplicativo.
            isIncludeAndroidResources = true
            all {
                it.inputs.property("alvoDoTeste", System.getenv("PONTE_TESTE_ALVO") ?: "")
                if (System.getenv("PONTE_TESTE_ALVO") != null) it.outputs.upToDateWhen { false }
                it.systemProperty("ponte.capturas", rootProject.file("../capturas/android").absolutePath)
                it.testLogging {
                    events("passed", "skipped", "failed")
                    showStandardStreams = true
                }
            }
        }
    }
    lint {
        // O aplicativo é só em português e não usa a biblioteca de compatibilidade.
        disable += setOf("HardcodedText", "SetTextI18n")
    }
}

dependencies {
    implementation(project(":core"))
    testImplementation("junit:junit:4.13.2")
    testImplementation("org.robolectric:robolectric:4.14.1")
}
