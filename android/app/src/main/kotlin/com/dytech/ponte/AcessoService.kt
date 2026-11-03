package com.dytech.ponte

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.media.projection.MediaProjectionManager
import android.net.ConnectivityManager
import android.net.Network
import android.net.wifi.WifiManager
import android.os.Build
import android.os.Handler
import android.os.IBinder
import android.os.Looper
import android.os.PowerManager
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicInteger
import ponte.core.Crypto
import ponte.core.HostPlatform
import ponte.core.HostSessionInfo
import ponte.core.IncomingRequest
import ponte.core.Permission
import ponte.core.PhoneInput
import ponte.core.PhoneKeyboard
import ponte.core.PonteException
import ponte.core.PonteHost
import ponte.core.SessionIo

/**
 * Deixa este celular acessível pelo ID, como o Ponte aberto num computador: mantém o registro no servidor,
 * atende os pedidos e conduz a sessão. Fica de pé como serviço em primeiro plano, com uma notificação fixa
 * (o Android exige que o usuário veja que o aplicativo está ativo).
 */
class AcessoService : Service(), HostPlatform {
    private val main = Handler(Looper.getMainLooper())
    private lateinit var config: AcessoStore
    private var host: PonteHost? = null
    private var captura: Captura? = null
    private var screenLock: PowerManager.WakeLock? = null
    private var awakeLock: PowerManager.WakeLock? = null
    private var wifiLock: WifiManager.WifiLock? = null
    private var networkCallback: ConnectivityManager.NetworkCallback? = null
    @Volatile private var statusText = "Conectando ao servidor..."
    @Volatile private var projectionAsked = false

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onCreate() {
        super.onCreate()
        config = AcessoStore(this)
        createChannels(this)
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        when (intent?.action) {
            ACTION_STOP -> {
                stopEverything()
                return START_NOT_STICKY
            }
            ACTION_END_SESSION -> host?.session?.stop()
            ACTION_PROJECTION -> onProjection(intent)
        }
        if (!config.enabled) {
            stopEverything()
            return START_NOT_STICKY
        }
        goForeground(projection = captura?.projecting == true)
        if (host == null) startHost()
        return START_STICKY
    }

    private fun startHost() {
        val key = try {
            AcessoIdentity.get(this)
        } catch (e: Exception) {
            statusText = "Não foi possível criar a identidade deste celular."
            refreshNotification()
            return
        }
        running = this
        val created = PonteHost(config, key, this)
        host = created
        created.start()
        holdAwake(true)
        val connectivity = getSystemService(ConnectivityManager::class.java)
        val callback = object : ConnectivityManager.NetworkCallback() {
            private var first = true
            override fun onAvailable(network: Network) {
                // A primeira chamada é a rede de agora; as seguintes são trocas (Wi-Fi ⇄ dados): refaz o registro.
                if (first) { first = false; return }
                created.reconnect()
            }
        }
        try {
            connectivity.registerDefaultNetworkCallback(callback)
            networkCallback = callback
        } catch (ignored: RuntimeException) {
        }
        notifyChanged()
    }

    private fun stopEverything() {
        networkCallback?.let { try { getSystemService(ConnectivityManager::class.java).unregisterNetworkCallback(it) } catch (ignored: RuntimeException) { } }
        networkCallback = null
        host?.stop()
        host = null
        captura?.close()
        captura = null
        holdScreen(false)
        holdAwake(false)
        if (running === this) running = null
        stopForeground(STOP_FOREGROUND_REMOVE)
        stopSelf()
        notifyChanged()
    }

    override fun onDestroy() {
        host?.stop()
        host = null
        captura?.close()
        captura = null
        holdScreen(false)
        holdAwake(false)
        networkCallback?.let { try { getSystemService(ConnectivityManager::class.java).unregisterNetworkCallback(it) } catch (ignored: RuntimeException) { } }
        if (running === this) running = null
        notifyChanged()
        super.onDestroy()
    }

    // ---------- notificação fixa ----------

    private fun goForeground(projection: Boolean) {
        val notification = buildNotification()
        if (Build.VERSION.SDK_INT >= 34) {
            var types = ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE
            if (projection) types = types or ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PROJECTION
            startForeground(NOTIFICATION_ID, notification, types)
        } else {
            startForeground(NOTIFICATION_ID, notification)
        }
    }

    private fun buildNotification(): Notification {
        val open = PendingIntent.getActivity(this, 0, Intent(this, AcessoActivity::class.java), PendingIntent.FLAG_IMMUTABLE)
        val session = host?.session
        val id = host?.id?.let { Crypto.formatId(it) } ?: ""
        val builder = Notification.Builder(this, CHANNEL_STATUS)
            .setSmallIcon(R.drawable.ic_notificacao)
            .setOngoing(true)
            .setContentIntent(open)
            .setShowWhen(false)
        if (session != null) {
            val end = PendingIntent.getService(this, 1, Intent(this, AcessoService::class.java).setAction(ACTION_END_SESSION), PendingIntent.FLAG_IMMUTABLE)
            builder.setContentTitle("${session.peerName} está acessando este celular")
                .setContentText("Toque em Encerrar para cortar o acesso agora.")
                .addAction(Notification.Action.Builder(null, "Encerrar", end).build())
        } else {
            builder.setContentTitle("Este celular pode ser acessado" + if (id.isNotEmpty()) " · ID $id" else "")
                .setContentText(statusText)
        }
        return builder.build()
    }

    private fun refreshNotification() {
        if (host == null) return
        getSystemService(NotificationManager::class.java).notify(NOTIFICATION_ID, buildNotification())
    }

    override fun status(online: Boolean, text: String) {
        statusText = text
        main.post {
            refreshNotification()
            notifyChanged()
        }
    }

    // ---------- pedidos de conexão ----------

    override fun askPermission(request: IncomingRequest): Permission = Pedidos.ask(this, request)

    // ---------- sessão ----------

    override fun openSession(session: HostSessionInfo): SessionIo {
        val capture = Captura(this)
        captura?.close()
        captura = capture
        projectionAsked = false
        main.post {
            holdScreen(true)
            refreshNotification()
            notifyChanged()
            requestProjection()
        }
        return PhoneSession(capture)
    }

    override fun sessionEnded(session: HostSessionInfo) {
        main.post {
            if (host?.session == null) {
                captura?.close()
                captura = null
                holdScreen(false)
                getSystemService(NotificationManager::class.java).cancel(NOTIFICATION_PROJECTION)
                ProjecaoActivity.closeIfOpen()
                // Sem a gravação de tela, o serviço volta ao tipo de sempre.
                if (config.enabled && host != null) goForeground(projection = false)
            }
            refreshNotification()
            notifyChanged()
        }
        ControleService.instance?.releaseAll()
    }

    /** Pede a gravação de tela: abre a tela do Android com "Começar" e, se ela não puder abrir agora, avisa por notificação. */
    private fun requestProjection() {
        if (captura == null || captura?.projecting == true) return
        projectionAsked = true
        val intent = Intent(this, ProjecaoActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_NO_ANIMATION)
        try {
            startActivity(intent)
        } catch (ignored: RuntimeException) {
        }
        val tap = PendingIntent.getActivity(this, 2, intent, PendingIntent.FLAG_IMMUTABLE)
        val notification = Notification.Builder(this, CHANNEL_REQUESTS)
            .setSmallIcon(R.drawable.ic_notificacao)
            .setContentTitle("Mostrar a tela para quem está acessando")
            .setContentText("Toque aqui e depois em Começar.")
            .setContentIntent(tap)
            .setAutoCancel(true)
            .build()
        getSystemService(NotificationManager::class.java).notify(NOTIFICATION_PROJECTION, notification)
    }

    private fun onProjection(intent: Intent) {
        getSystemService(NotificationManager::class.java).cancel(NOTIFICATION_PROJECTION)
        val code = intent.getIntExtra(EXTRA_CODE, 0)
        @Suppress("DEPRECATION")
        val data: Intent? = intent.getParcelableExtra(EXTRA_DATA)
        val capture = captura
        if (data == null || capture == null || host?.session == null) return
        // O Android 14 só entrega a gravação a um serviço que já está no ar com o tipo "gravação de tela".
        try {
            goForeground(projection = true)
            val projection = getSystemService(MediaProjectionManager::class.java).getMediaProjection(code, data) ?: return
            capture.start(projection)
        } catch (e: RuntimeException) {
            goForeground(projection = false)
        }
    }

    private inner class PhoneSession(private val capture: Captura) : SessionIo {
        private val keyboard = PhoneKeyboard()

        override fun size(): Pair<Int, Int> = capture.size()

        override fun frame(quality: Int, full: Boolean): ByteArray? = capture.frame(quality, full, ControleService.instance)

        override fun mouse(x: Int, y: Int, action: Int, wheel: Int) {
            ControleService.instance?.mouse(x, y, action, wheel)
        }

        override fun key(virtualKey: Int, up: Boolean) {
            val control = ControleService.instance ?: return
            when (val input = keyboard.key(virtualKey, up)) {
                is PhoneInput.Type -> control.type(input.text)
                is PhoneInput.Do -> control.perform(input.action)
                null -> {}
            }
        }

        override fun text(text: String) {
            val control = ControleService.instance ?: return
            // Quebras de linha vêm como Enter: nos campos de uma linha, ele é o "OK" do teclado.
            val parts = text.replace("\r", "").split('\n')
            parts.forEachIndexed { index, part ->
                if (part.isNotEmpty()) control.type(part)
                if (index < parts.size - 1) control.perform(ponte.core.PhoneAction.ENTER)
            }
        }

        override fun clipboard(text: String) {
            ControleService.instance?.clipboard(text)
        }

        override fun problem(): String? {
            val control = ControleService.instance
            if (!capture.projecting) {
                return if (control != null && Build.VERSION.SDK_INT >= 30)
                    "A imagem está lenta: falta alguém tocar em \"Começar\" no celular para ela ficar fluida."
                else "Esperando alguém tocar em \"Começar\" no celular para mostrar a tela."
            }
            if (control == null) return "O controle deste celular está desligado: dá só para ver. Para controlar, ligue o Ponte no celular em Configurações > Acessibilidade."
            return null
        }

        override fun close() {
            keyboard.reset()
        }
    }

    // ---------- tela acesa e celular acordado ----------

    @Suppress("DEPRECATION")
    private fun holdScreen(on: Boolean) {
        if (on) {
            if (screenLock == null) {
                // Quem acessa precisa da tela acesa: ela acende ao começar e fica acesa durante a sessão.
                screenLock = getSystemService(PowerManager::class.java)
                    .newWakeLock(PowerManager.SCREEN_BRIGHT_WAKE_LOCK or PowerManager.ACQUIRE_CAUSES_WAKEUP, "Ponte:sessao")
                    .apply { acquire(12 * 60 * 60 * 1000L) }
            }
            if (wifiLock == null && Build.VERSION.SDK_INT >= 29) {
                wifiLock = (applicationContext.getSystemService(WIFI_SERVICE) as WifiManager)
                    .createWifiLock(WifiManager.WIFI_MODE_FULL_LOW_LATENCY, "Ponte:sessao").apply { setReferenceCounted(false); acquire() }
            }
        } else {
            screenLock?.let { if (it.isHeld) it.release() }
            screenLock = null
            wifiLock?.let { if (it.isHeld) it.release() }
            wifiLock = null
        }
    }

    // O registro no servidor precisa do PING a cada 25 s; com o celular dormindo ele para e o servidor tira o ID do ar.
    private fun holdAwake(on: Boolean) {
        if (on && config.alwaysReady) {
            if (awakeLock == null) {
                awakeLock = getSystemService(PowerManager::class.java)
                    .newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "Ponte:pronto").apply { setReferenceCounted(false); acquire() }
            }
        } else {
            awakeLock?.let { if (it.isHeld) it.release() }
            awakeLock = null
        }
    }

    companion object {
        const val ACTION_START = "com.dytech.ponte.acesso.LIGAR"
        const val ACTION_STOP = "com.dytech.ponte.acesso.DESLIGAR"
        const val ACTION_END_SESSION = "com.dytech.ponte.acesso.ENCERRAR"
        const val ACTION_PROJECTION = "com.dytech.ponte.acesso.GRAVACAO"
        const val EXTRA_CODE = "codigo"
        const val EXTRA_DATA = "dados"
        const val CHANNEL_STATUS = "acesso"
        const val CHANNEL_REQUESTS = "pedidos"
        private const val NOTIFICATION_ID = 7411
        private const val NOTIFICATION_PROJECTION = 7413

        @Volatile private var running: AcessoService? = null
        private val listeners = java.util.concurrent.CopyOnWriteArrayList<() -> Unit>()

        /** O ID deste celular, a frase do estado e quem está acessando agora (para a tela de Acesso). */
        val id: String? get() = running?.host?.id
        val online: Boolean get() = running?.host?.online == true
        val status: String get() = running?.statusText ?: ""
        val session: HostSessionInfo? get() = running?.host?.session

        fun addListener(listener: () -> Unit) = listeners.add(listener)
        fun removeListener(listener: () -> Unit) = listeners.remove(listener)
        private fun notifyChanged() = listeners.forEach { it() }

        fun start(context: Context) {
            val intent = Intent(context, AcessoService::class.java).setAction(ACTION_START)
            if (Build.VERSION.SDK_INT >= 26) context.startForegroundService(intent) else context.startService(intent)
        }

        fun stop(context: Context) {
            context.startService(Intent(context, AcessoService::class.java).setAction(ACTION_STOP))
        }

        fun createChannels(context: Context) {
            val manager = context.getSystemService(NotificationManager::class.java)
            manager.createNotificationChannel(
                NotificationChannel(CHANNEL_STATUS, "Acesso a este celular", NotificationManager.IMPORTANCE_LOW).apply {
                    description = "Mostra que este celular pode ser acessado e quem está acessando."
                    setShowBadge(false)
                },
            )
            manager.createNotificationChannel(
                NotificationChannel(CHANNEL_REQUESTS, "Pedidos de acesso", NotificationManager.IMPORTANCE_HIGH).apply {
                    description = "Quando alguém pede para acessar este celular."
                },
            )
        }
    }
}

/**
 * Pedidos de conexão esperando resposta de quem está com o celular. A pergunta aparece numa janela (quando o
 * Android deixa abrir) e numa notificação com os botões; a primeira resposta vale, e sem resposta em 30 s o pedido
 * é recusado.
 */
object Pedidos {
    class Pending(val id: Int, val request: IncomingRequest) {
        internal val done = CountDownLatch(1)
        @Volatile internal var answer = Permission.REFUSE
        val expires: Long = System.currentTimeMillis() + TIMEOUT_MS
    }

    const val TIMEOUT_MS = 30_000L
    const val EXTRA_ID = "pedido"
    const val EXTRA_ANSWER = "resposta"
    private val counter = AtomicInteger(100)
    private val pending = ConcurrentHashMap<Int, Pending>()

    fun get(id: Int): Pending? = pending[id]

    fun answer(context: Context, id: Int, permission: Permission) {
        val item = pending.remove(id) ?: return
        item.answer = permission
        item.done.countDown()
        context.getSystemService(NotificationManager::class.java).cancel(id)
    }

    fun ask(context: Context, request: IncomingRequest): Permission {
        val item = Pending(counter.incrementAndGet(), request)
        pending[item.id] = item
        val who = request.name.ifEmpty { "Alguém" } + if (request.user.isNotEmpty() && request.user != request.name) " (${request.user})" else ""
        val show = Intent(context, PedidoActivity::class.java).putExtra(EXTRA_ID, item.id)
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_MULTIPLE_TASK)
        fun action(permission: Permission, label: String) = Notification.Action.Builder(
            null, label,
            PendingIntent.getBroadcast(
                context, item.id * 4 + permission.ordinal,
                Intent(context, PedidoReceiver::class.java).putExtra(EXTRA_ID, item.id).putExtra(EXTRA_ANSWER, permission.name),
                PendingIntent.FLAG_IMMUTABLE,
            ),
        ).build()
        val notification = Notification.Builder(context, AcessoService.CHANNEL_REQUESTS)
            .setSmallIcon(R.drawable.ic_notificacao)
            .setContentTitle("$who quer acessar este celular")
            .setContentText("Permitir? Sem resposta em 30 segundos, o pedido é recusado.")
            .setCategory(Notification.CATEGORY_CALL)
            .setContentIntent(PendingIntent.getActivity(context, item.id, show, PendingIntent.FLAG_IMMUTABLE))
            .setFullScreenIntent(PendingIntent.getActivity(context, item.id + 50_000, show, PendingIntent.FLAG_IMMUTABLE), true)
            .addAction(action(Permission.REFUSE, "Recusar"))
            .addAction(action(Permission.ONCE, "Permitir"))
            .apply { if (request.canRemember) addAction(action(Permission.ALWAYS, "Sempre permitir")) }
            .setTimeoutAfter(TIMEOUT_MS)
            .setAutoCancel(true)
            .build()
        context.getSystemService(NotificationManager::class.java).notify(item.id, notification)
        try {
            context.startActivity(show)
        } catch (ignored: RuntimeException) {
        }
        item.done.await(TIMEOUT_MS, TimeUnit.MILLISECONDS)
        pending.remove(item.id)
        context.getSystemService(NotificationManager::class.java).cancel(item.id)
        return item.answer
    }
}

/** Os botões da notificação do pedido. */
class PedidoReceiver : android.content.BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        val answer = try { Permission.valueOf(intent.getStringExtra(Pedidos.EXTRA_ANSWER) ?: "") } catch (e: IllegalArgumentException) { Permission.REFUSE }
        Pedidos.answer(context, intent.getIntExtra(Pedidos.EXTRA_ID, 0), answer)
    }
}

/** Liga o acesso de novo quando o celular é ligado (se a pessoa deixou assim). */
class InicioReceiver : android.content.BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        if (intent.action != Intent.ACTION_BOOT_COMPLETED && intent.action != Intent.ACTION_MY_PACKAGE_REPLACED) return
        val config = AcessoStore(context)
        if (config.enabled && (config.startOnBoot || intent.action == Intent.ACTION_MY_PACKAGE_REPLACED)) {
            try { AcessoService.start(context) } catch (ignored: RuntimeException) { }
        }
    }
}
