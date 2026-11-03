package com.dytech.ponte

import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import java.io.File
import java.security.KeyFactory
import java.security.KeyPairGenerator
import java.security.KeyStore
import java.security.SecureRandom
import java.security.spec.ECGenParameterSpec
import java.security.spec.PKCS8EncodedKeySpec
import java.util.Base64
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec
import ponte.core.HostConfig
import ponte.core.HostCrypto
import ponte.core.HostKey
import ponte.core.Json
import ponte.core.SelfSignedCertificate
import ponte.core.bool
import ponte.core.long
import ponte.core.objects
import ponte.core.str

/** Um aparelho que este celular deixou entrar direto (sem senha nem aceite). */
class AutorizadoInfo(val keyHash: String, val id: String, val name: String, val user: String, val added: Long, val lastUsed: Long, val viaPassword: Boolean)

/**
 * O que este celular guarda para ser acessado: ligado ou não, senha, autorizados e o tempo sem uso. A senha fica
 * como no Ponte do Windows: só o sal e o verificador (PBKDF2), nunca a senha.
 */
class AcessoStore(context: Context) : HostConfig {
    private val app = context.applicationContext
    private val prefs = app.getSharedPreferences("acesso", Context.MODE_PRIVATE)
    private val store = Store(app)

    override val relayServer: String get() = store.relayServer
    override val relayKey: String get() = store.relayKey
    override val hostName: String get() = store.deviceName
    override val version: String get() = BuildConfig.VERSION_NAME
    override val passwordSalt: String get() = prefs.getString("sal", "") ?: ""
    override val passwordVerifier: String get() = prefs.getString("verificador", "") ?: ""
    override val iterations: Int get() = prefs.getInt("iteracoes", ITERATIONS)
    override val idleSeconds: Int get() = idleMinutes * 60

    /** O celular fica acessível (o serviço com a notificação fica rodando). */
    var enabled: Boolean
        get() = prefs.getBoolean("ligado", false)
        set(value) = prefs.edit().putBoolean("ligado", value).apply()

    /** Ligar o acesso sozinho quando o celular é ligado. */
    var startOnBoot: Boolean
        get() = prefs.getBoolean("aoLigar", true)
        set(value) = prefs.edit().putBoolean("aoLigar", value).apply()

    /**
     * Manter o celular acordado enquanto o acesso está ligado. Dormindo, o celular para de avisar o servidor e sai
     * do ar até alguém mexer nele; acordado, gasta um pouco mais de bateria.
     */
    var alwaysReady: Boolean
        get() = prefs.getBoolean("semprePronto", true)
        set(value) = prefs.edit().putBoolean("semprePronto", value).apply()

    /** Minutos sem uso até a sessão ser encerrada (0 é nunca), quando quem acessa não pede outro tempo. */
    var idleMinutes: Int
        get() = prefs.getInt("inatividade", 0)
        set(value) = prefs.edit().putInt("inatividade", value.coerceIn(0, 7 * 24 * 60)).apply()

    val hasPassword: Boolean get() = passwordVerifier.isNotEmpty()

    /** Define a senha (demora um ou dois segundos: são 200 mil rodadas) ou, com vazio, tira a senha. */
    fun setPassword(password: String?) {
        if (password.isNullOrEmpty()) {
            prefs.edit().remove("sal").remove("verificador").apply()
            return
        }
        val salt = ByteArray(16).also { SecureRandom().nextBytes(it) }
        val verifier = HostCrypto.verifier(password, salt, ITERATIONS)
        val encoder = Base64.getEncoder()
        prefs.edit().putString("sal", encoder.encodeToString(salt)).putString("verificador", encoder.encodeToString(verifier))
            .putInt("iteracoes", ITERATIONS).apply()
    }

    // ---------- autorizados ----------

    fun trusted(): List<AutorizadoInfo> = synchronized(LOCK) {
        val items = try { Json.parseObject(prefs.getString("autorizados", null) ?: "{}").objects("itens") } catch (e: IllegalArgumentException) { emptyList() }
        items.map {
            AutorizadoInfo(it.str("chave"), it.str("id"), it.str("nome"), it.str("usuario"), it.long("desde"), it.long("ultimo"), it.bool("senha"))
        }
    }

    private fun write(list: List<AutorizadoInfo>) {
        val items = list.map {
            mapOf("chave" to it.keyHash, "id" to it.id, "nome" to it.name, "usuario" to it.user, "desde" to it.added, "ultimo" to it.lastUsed, "senha" to it.viaPassword)
        }
        prefs.edit().putString("autorizados", Json.write(mapOf("itens" to items))).apply()
    }

    override fun isTrusted(keyHash: String): Boolean = trusted().any { it.keyHash == keyHash }

    override fun trust(keyHash: String, id: String, name: String, user: String, viaPassword: Boolean): Boolean = synchronized(LOCK) {
        val all = trusted().toMutableList()
        val now = System.currentTimeMillis()
        val index = all.indexOfFirst { it.keyHash == keyHash }
        if (index >= 0) {
            val old = all[index]
            all[index] = AutorizadoInfo(keyHash, id.ifEmpty { old.id }, name, user, old.added, now, old.viaPassword)
        } else {
            if (all.size >= MAX_TRUSTED) return false
            all.add(AutorizadoInfo(keyHash, id, name, user, now, now, viaPassword))
        }
        write(all)
        true
    }

    override fun touchTrusted(keyHash: String, name: String, user: String): Boolean = synchronized(LOCK) {
        val all = trusted().toMutableList()
        val index = all.indexOfFirst { it.keyHash == keyHash }
        if (index < 0) return false
        val old = all[index]
        all[index] = AutorizadoInfo(keyHash, old.id, name, user, old.added, System.currentTimeMillis(), old.viaPassword)
        write(all)
        true
    }

    fun removeTrusted(keyHash: String) = synchronized(LOCK) { write(trusted().filter { it.keyHash != keyHash }) }

    companion object {
        const val ITERATIONS = 200_000
        private const val MAX_TRUSTED = 50
        private val LOCK = Any()
    }
}

/**
 * A identidade deste celular como acessado: chave ECDSA P-256 e certificado autoassinado, como os do Ponte do
 * Windows (é dela que sai o ID). A chave privada precisa ir para o TLS, então ela fica num arquivo do aplicativo,
 * cifrado com uma chave AES que não sai do cofre do Android.
 */
object AcessoIdentity {
    private const val ALIAS = "ponte-acesso"

    /** Só para os testes, que rodam fora de um aparelho e não têm o cofre do Android. */
    internal var replacement: HostKey? = null

    @Volatile private var cached: HostKey? = null

    @Synchronized
    fun get(context: Context): HostKey {
        replacement?.let { return it }
        cached?.let { return it }
        val file = File(File(context.applicationContext.filesDir, "acesso"), "identidade.json")
        val key = (if (file.exists()) read(file) else null) ?: create(file)
        cached = key
        return key
    }

    private fun read(file: File): HostKey? = try {
        val data = Json.parseObject(file.readText())
        val decoder = Base64.getDecoder()
        val sealed = decoder.decode(data.str("chave"))
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.DECRYPT_MODE, vaultKey(), GCMParameterSpec(128, sealed.copyOfRange(0, 12)))
        val pkcs8 = cipher.doFinal(sealed, 12, sealed.size - 12)
        HostKey(KeyFactory.getInstance("EC").generatePrivate(PKCS8EncodedKeySpec(pkcs8)), decoder.decode(data.str("certificado")))
    } catch (e: Exception) {
        // Arquivo estragado ou cofre apagado (backup restaurado em outro aparelho): cria outra identidade.
        null
    }

    private fun create(file: File): HostKey {
        val pair = KeyPairGenerator.getInstance("EC").apply { initialize(ECGenParameterSpec("secp256r1")) }.generateKeyPair()
        val certificate = SelfSignedCertificate.create(pair)
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.ENCRYPT_MODE, vaultKey())
        val sealed = cipher.iv + cipher.doFinal(pair.private.encoded)
        val encoder = Base64.getEncoder()
        file.parentFile?.mkdirs()
        val temp = File(file.parentFile, file.name + ".novo")
        temp.writeText(Json.write(mapOf("chave" to encoder.encodeToString(sealed), "certificado" to encoder.encodeToString(certificate))))
        if (!temp.renameTo(file)) { file.delete(); temp.renameTo(file) }
        return HostKey(pair.private, certificate)
    }

    private fun vaultKey(): SecretKey {
        val keyStore = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        (keyStore.getKey(ALIAS, null) as? SecretKey)?.let { return it }
        val generator = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore")
        generator.init(
            KeyGenParameterSpec.Builder(ALIAS, KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT)
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                .setKeySize(256)
                .build()
        )
        return generator.generateKey()
    }
}
