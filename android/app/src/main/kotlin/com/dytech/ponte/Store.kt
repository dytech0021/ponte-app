package com.dytech.ponte

import android.content.Context
import android.os.Build
import android.provider.Settings
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import java.security.KeyPairGenerator
import java.security.KeyStore
import java.security.PrivateKey
import java.security.spec.ECGenParameterSpec
import ponte.core.EcIdentity
import ponte.core.Identity
import ponte.core.Json
import ponte.core.Target
import ponte.core.bool
import ponte.core.long
import ponte.core.objects
import ponte.core.str

/** Um computador guardado na lista, com nome. */
class SavedComputer(
    val key: String,
    val address: String,
    var name: String,
    /** Pedir ao computador para este aparelho entrar sem aprovação nas próximas vezes. */
    var autoAccess: Boolean,
    /** Na última conexão o computador confirmou que este aparelho está autorizado. */
    var paired: Boolean,
    /** Está guardado na conta (quando há conta). paired é deste aparelho e não vai para a conta. */
    var synced: Boolean = false,
    /** Mudou aqui e ainda não foi para a conta. */
    var dirty: Boolean = false,
) {
    val title: String get() = name.ifEmpty { address }
}

class RecentComputer(val address: String, val name: String, val time: Long)

/** O que o aplicativo guarda entre uma abertura e outra, nas preferências privadas dele. */
class Store(context: Context) {
    private val appContext = context.applicationContext
    private val prefs = appContext.getSharedPreferences("ponte", Context.MODE_PRIVATE)

    // Servidor e chave: vale o que o usuário digitou em Configurações; sem isso, o que veio embutido no aplicativo.
    var relayServer: String
        get() = prefs.getString("servidor", null) ?: BuildConfig.RELAY_SERVER
        set(value) = prefs.edit().putString("servidor", value.trim()).apply()

    var relayKey: String
        get() = prefs.getString("chave", null) ?: BuildConfig.RELAY_KEY
        set(value) = prefs.edit().putString("chave", value.trim()).apply()

    /** Nome com que este aparelho aparece no pedido de conexão e na lista de autorizados do computador. */
    var deviceName: String
        get() = prefs.getString("nome", null)?.takeIf { it.isNotBlank() } ?: defaultDeviceName()
        set(value) = prefs.edit().putString("nome", value.trim()).apply()

    var touchpad: Boolean
        get() = prefs.getBoolean("touchpad", false)
        set(value) = prefs.edit().putBoolean("touchpad", value).apply()

    /** Quando o aplicativo procurou atualização pela última vez (milissegundos desde 1970). */
    var lastUpdateCheck: Long
        get() = prefs.getLong("atualizacao", 0L)
        set(value) = prefs.edit().putLong("atualizacao", value).apply()

    /** A sessão abre em tela cheia (sem barra de comandos, imagem cobrindo o aparelho). */
    var fullScreen: Boolean
        get() = prefs.getBoolean("telaCheia", false)
        set(value) = prefs.edit().putBoolean("telaCheia", value).apply()

    /** A transmissão de tela abre sem as barras do Android (a última escolha na tela de assistir). */
    var watchFullScreen: Boolean
        get() = prefs.getBoolean("assistirTelaCheia", true)
        set(value) = prefs.edit().putBoolean("assistirTelaCheia", value).apply()

    /** Ouvir o som do computador durante a sessão. */
    var sound: Boolean
        get() = prefs.getBoolean("som", true)
        set(value) = prefs.edit().putBoolean("som", value).apply()

    var themeMode: ThemeMode
        get() = ThemeMode.values().firstOrNull { it.value == prefs.getString("tema", null) } ?: ThemeMode.SYSTEM
        set(value) = prefs.edit().putString("tema", value.value).apply()

    /** Onde fica a miniatura de um computador acessado: a última imagem vista da tela dele, reduzida. */
    fun thumbnailFile(key: String): java.io.File =
        java.io.File(java.io.File(appContext.filesDir, "miniaturas"), ponte.core.Crypto.hex(ponte.core.Crypto.sha256(key.toByteArray())).take(20) + ".jpg")

    private fun defaultDeviceName(): String {
        val chosen = try { Settings.Global.getString(appContext.contentResolver, "device_name") } catch (e: RuntimeException) { null }
        return chosen?.takeIf { it.isNotBlank() } ?: Build.MODEL ?: "Android"
    }

    // ---------- computadores salvos ----------

    fun saved(): List<SavedComputer> = list("salvos").map {
        SavedComputer(it.str("key"), it.str("address"), it.str("name"), it.bool("auto"), it.bool("paired"), it.bool("conta"), it.bool("mudou"))
    }.sortedBy { it.title.lowercase() }

    fun findSaved(key: String): SavedComputer? = saved().firstOrNull { it.key == key }

    fun saveComputer(targetText: String, name: String, autoAccess: Boolean): Unit = synchronized(LOCK) {
        val target = Target.parse(targetText)
        val all = saved().toMutableList()
        val existing = all.firstOrNull { it.key == target.key }
        if (existing != null) {
            existing.name = name
            existing.autoAccess = autoAccess
            existing.dirty = true
        } else {
            all.add(SavedComputer(target.key, target.shown, name, autoAccess, false, dirty = true))
        }
        writeSaved(all)
        pendingDeletes = pendingDeletes - target.key
    }

    fun removeSaved(key: String) {
        synchronized(LOCK) {
            val all = saved()
            if (all.any { it.key == key && it.synced } && key !in pendingDeletes) pendingDeletes = pendingDeletes + key
            writeSaved(all.filter { it.key != key })
        }
        // Remover explicitamente um salvo também descarta a imagem privada da última sessão.
        deleteThumbnail(key)
    }

    private fun writeSaved(all: List<SavedComputer>) = put("salvos", all.map {
        mapOf("key" to it.key, "address" to it.address, "name" to it.name, "auto" to it.autoAccess, "paired" to it.paired,
            "conta" to it.synced, "mudou" to it.dirty)
    })

    // ---------- conta (opcional) ----------

    /** Computadores apagados aqui que ainda precisam sair da conta (o apagar pode ter sido sem internet). */
    private var pendingDeletes: List<String>
        get() = list("contaApagar").map { it.str("key") }
        set(value) = put("contaApagar", value.map { mapOf("key" to it) })

    /** A conta em que este aparelho entrou, ou null. A mesma instância para o aplicativo todo. */
    fun account(): Account? {
        synchronized(LOCK) {
            current?.let { return it }
            val refresh = TokenVault.open(prefs.getString("contaToken", null) ?: "") ?: return null
            val restored = Account.restore(prefs.getString("contaEmail", null) ?: "", prefs.getString("contaUsuario", null) ?: "", refresh)
            restored?.onTokenChanged = { storeAccount(it) }
            current = restored
            return restored
        }
    }

    /** Grava a conta (o token de renovação vai cifrado pelo cofre do Android). */
    fun storeAccount(account: Account): Unit = synchronized(LOCK) {
        account.onTokenChanged = { storeAccount(it) }
        current = account
        prefs.edit()
            .putString("contaEmail", account.email)
            .putString("contaUsuario", account.userId)
            .putString("contaToken", TokenVault.seal(account.refreshToken))
            .apply()
    }

    /**
     * Junta a lista daqui com a da conta: o que está na conta vem para cá, o que foi salvo ou mudado aqui vai para
     * lá, e o que foi apagado em outro aparelho sai daqui. Bloqueante. Devolve quantos chegaram e quantos saíram.
     */
    fun syncAccount(account: Account): Pair<Int, Int> {
        for (key in synchronized(LOCK) { pendingDeletes }) {
            account.delete(key)
            synchronized(LOCK) { pendingDeletes = pendingDeletes - key }
        }
        val remote = account.list()
        val push: List<Account.Computer>
        var added = 0
        val gone = mutableListOf<String>()
        synchronized(LOCK) {
            val all = saved().toMutableList()
            val remoteKeys = remote.map { it.key }.toSet()
            for (r in remote) {
                val local = all.firstOrNull { it.key == r.key }
                if (local == null) {
                    all.add(SavedComputer(r.key, r.address, r.name, r.autoAccess, false, synced = true))
                    added++
                } else if (!local.dirty) {
                    local.name = r.name
                    local.autoAccess = r.autoAccess
                    local.synced = true
                }
            }
            all.filter { it.synced && !it.dirty && it.key !in remoteKeys }.forEach {
                all.remove(it)
                gone.add(it.key)
            }
            push = all.filter { it.dirty || !it.synced }.map { Account.Computer(it.key, it.address, it.name, it.autoAccess) }
            writeSaved(all)
        }
        gone.forEach { deleteThumbnail(it) }
        account.upsert(push)
        synchronized(LOCK) {
            val pushed = push.map { it.key }.toSet()
            val all = saved()
            all.filter { it.key in pushed }.forEach {
                it.synced = true
                it.dirty = false
            }
            writeSaved(all)
        }
        lastAccountSync = System.currentTimeMillis()
        return added to gone.size
    }

    /**
     * Sai da conta neste aparelho: os computadores que vieram dela (ou foram para ela) somem daqui, para a próxima
     * pessoa que usar o aplicativo não ver a lista de outra. O que foi salvo sem conta fica.
     */
    fun signOutAccount() {
        val mine = synchronized(LOCK) {
            val all = saved()
            val mine = all.filter { it.synced }.map { it.key }
            writeSaved(all.filter { !it.synced })
            pendingDeletes = emptyList()
            current = null
            prefs.edit().remove("contaEmail").remove("contaUsuario").remove("contaToken").apply()
            mine
        }
        mine.forEach { deleteThumbnail(it) }
    }

    companion object {
        private val LOCK = Any()
        private var current: Account? = null
        /** Quando a lista foi conferida com a conta pela última vez (nesta execução). */
        @Volatile var lastAccountSync = 0L
    }

    // ---------- recentes e identidades vistas ----------

    fun recents(): List<RecentComputer> = list("recentes").map { RecentComputer(it.str("address"), it.str("name"), it.long("time")) }

    fun removeRecent(address: String) {
        val key = Target.parse(address).key
        writeRecents(recents().filter { Target.parse(it.address).key != key })
        if (findSaved(key) == null) deleteThumbnail(key)
    }

    private fun deleteThumbnail(key: String) {
        val file = thumbnailFile(key)
        file.delete()
        java.io.File(file.path + ".tmp").delete()
    }

    private fun writeRecents(all: List<RecentComputer>) = put("recentes", all.map {
        mapOf("address" to it.address, "name" to it.name, "time" to it.time)
    })

    /** Identidade vista da última vez neste destino, ou null na primeira conexão. */
    fun knownFingerprint(key: String): String? = prefs.getString("identidade:$key", null)

    /** Depois de uma conexão feita: a identidade vista, a lista de recentes e o estado da autorização no salvo. */
    fun recordConnection(target: Target, hostName: String, fingerprint: String, paired: Boolean) {
        prefs.edit().putString("identidade:${target.key}", fingerprint).apply()
        val all = synchronized(LOCK) {
            val all = saved()
            all.firstOrNull { it.key == target.key }?.let {
                it.paired = paired
                writeSaved(all)
            }
            all
        }
        val recents = recents().filter { Target.parse(it.address).key != target.key }.toMutableList()
        recents.add(0, RecentComputer(target.shown, hostName, System.currentTimeMillis()))
        writeRecents(recents.take(12))
        val savedKeys = all.map { it.key }.toSet()
        recents.drop(12).map { Target.parse(it.address).key }.distinct()
            .filter { it !in savedKeys }.forEach { deleteThumbnail(it) }
    }

    private fun list(name: String): List<Map<String, Any?>> = try {
        Json.parseObject(prefs.getString(name, null) ?: "{}").objects("itens")
    } catch (e: IllegalArgumentException) {
        emptyList()
    }

    private fun put(name: String, items: List<Map<String, Any?>>) =
        prefs.edit().putString(name, Json.write(mapOf("itens" to items))).apply()
}

/**
 * A identidade deste aparelho: uma chave ECDSA P-256 guardada no cofre do Android. A parte privada
 * não sai do cofre, nem para o próprio aplicativo; é com ela que um computador reconhece o aparelho
 * que ele autorizou.
 */
object DeviceIdentity {
    private const val ALIAS = "ponte-identidade"

    /** Só para os testes, que rodam fora de um aparelho e não têm o cofre do Android. */
    internal var replacement: Identity? = null

    fun get(): Identity {
        replacement?.let { return it }
        val keyStore = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        if (!keyStore.containsAlias(ALIAS)) {
            val generator = KeyPairGenerator.getInstance(KeyProperties.KEY_ALGORITHM_EC, "AndroidKeyStore")
            generator.initialize(
                KeyGenParameterSpec.Builder(ALIAS, KeyProperties.PURPOSE_SIGN)
                    .setAlgorithmParameterSpec(ECGenParameterSpec("secp256r1"))
                    .setDigests(KeyProperties.DIGEST_SHA256)
                    .build()
            )
            generator.generateKeyPair()
        }
        val privateKey = keyStore.getKey(ALIAS, null) as PrivateKey
        return EcIdentity(privateKey, keyStore.getCertificate(ALIAS).publicKey)
    }
}
