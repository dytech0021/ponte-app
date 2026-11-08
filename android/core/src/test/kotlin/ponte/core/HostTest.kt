package ponte.core

import java.io.DataInputStream
import java.io.InputStream
import java.io.OutputStream
import java.math.BigInteger
import java.net.ServerSocket
import java.net.Socket
import java.security.KeyFactory
import java.security.KeyPairGenerator
import java.security.Signature
import java.security.interfaces.ECPublicKey
import java.security.spec.ECGenParameterSpec
import java.security.spec.ECPoint
import java.security.spec.ECPublicKeySpec
import java.util.Base64
import java.util.Collections
import java.util.concurrent.ArrayBlockingQueue
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import javax.crypto.Cipher
import javax.crypto.KeyAgreement
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.SecretKeySpec
import kotlin.concurrent.thread
import kotlin.test.Test
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

/** O celular sendo acessado: o núcleo inteiro, com o próprio cliente do aplicativo do outro lado. */
class HostTest {
    private fun ecPair() = KeyPairGenerator.getInstance("EC").apply { initialize(ECGenParameterSpec("secp256r1")) }.generateKeyPair()

    /** A chave do "celular"; com [config], ele ganha também o bilhete da conta (1.17.0) preso a ela. */
    private fun newHostKey(config: Config? = null): HostKey {
        val pair = ecPair()
        return HostKey(pair.private, SelfSignedCertificate.create(pair)).also { config?.key = it }
    }

    @Test
    fun certificadoAutoassinadoComOIdDaChave() {
        val pair = ecPair()
        val der = SelfSignedCertificate.create(pair)
        val cert = SelfSignedCertificate.parse(der)
        cert.verify(pair.public)
        cert.checkValidity()
        assertEquals("CN=Ponte", cert.subjectX500Principal.name)
        assertEquals("SHA256withECDSA", cert.sigAlgName)
        assertContentEquals(pair.public.encoded, cert.publicKey.encoded)
        val key = HostKey(pair.private, der)
        assertEquals(Crypto.idFromSpki(pair.public.encoded), key.id)
        // Datas depois de 2049 usam outro formato: o certificado de 30 anos precisa continuar legível.
        assertTrue(cert.notAfter.time - System.currentTimeMillis() > 29L * 365 * 24 * 3600 * 1000)
    }

    @Test
    fun assinaturaVaiEVoltaEntreOsFormatos() {
        val pair = ecPair()
        repeat(40) { round ->
            val data = ByteArray(40) { (it * round).toByte() }
            val p1363 = HostCrypto.sign(pair.private, data)
            assertEquals(64, p1363.size)
            val verifier = Signature.getInstance("SHA256withECDSA").apply { initVerify(pair.public); update(data) }
            assertTrue(verifier.verify(HostCrypto.p1363ToDer(p1363)), "rodada $round")
            assertContentEquals(p1363, Crypto.derToP1363(HostCrypto.p1363ToDer(p1363)))
        }
    }

    @Test
    fun confereAAssinaturaDeQuemAcessa() {
        val pair = ecPair()
        val viewer = EcIdentity(pair.private, pair.public)
        val nonce = ByteArray(32) { 9 }
        val certHash = ByteArray(32) { 4 }
        val signature = Base64.getEncoder().encodeToString(viewer.sign(Crypto.pairingMessage(nonce, certHash)))
        assertTrue(HostCrypto.verifyPairing(viewer.spki, nonce, certHash, signature))
        assertFalse(HostCrypto.verifyPairing(viewer.spki, ByteArray(32), certHash, signature))
        assertFalse(HostCrypto.verifyPairing(viewer.spki, nonce, certHash, null))
        assertFalse(HostCrypto.verifyPairing(byteArrayOf(1, 2, 3), nonce, certHash, signature))
        assertFalse(HostCrypto.verifyPairing(viewer.spki, nonce, certHash, "não é base64"))
    }

    @Test
    fun quadroSoComOQueMudou() {
        val width = 300
        val height = 200
        val pixels = IntArray(width * height) { 0xFF000000.toInt() }
        val encoded = ArrayList<IntArray>()
        val encoder = TileEncoder { x, y, w, h, q -> encoded.add(intArrayOf(x, y, w, h, q)); byteArrayOf(7, 7) }
        val builder = FrameBuilder(tile = 128)

        val first = Session.parseFrame(builder.build(pixels, width, height, 60, false, encoder)!!)
        assertEquals(300, first.width)
        // Primeira vez: a tela inteira, uma faixa por linha de blocos.
        assertEquals(listOf(listOf(0, 0, 300, 128), listOf(0, 128, 300, 72)), first.tiles.map { listOf(it.x, it.y, it.width, it.height) })

        assertNull(builder.build(pixels, width, height, 60, false, encoder))

        pixels[150 * width + 260] = 0xFFFFFFFF.toInt()
        val changed = Session.parseFrame(builder.build(pixels, width, height, 60, false, encoder)!!)
        assertEquals(listOf(listOf(256, 128, 44, 72)), changed.tiles.map { listOf(it.x, it.y, it.width, it.height) })

        // Dois blocos vizinhos na mesma faixa viram um retângulo só.
        pixels[10 * width + 5] = 1
        pixels[10 * width + 130] = 1
        val joined = Session.parseFrame(builder.build(pixels, width, height, 60, false, encoder)!!)
        assertEquals(listOf(listOf(0, 0, 256, 128)), joined.tiles.map { listOf(it.x, it.y, it.width, it.height) })

        // Pedido de tela inteira, ou tamanho novo (o celular girou): tudo de novo.
        assertEquals(2, Session.parseFrame(builder.build(pixels, width, height, 60, true, encoder)!!).tiles.size)
        val rotated = Session.parseFrame(builder.build(IntArray(height * width), height, width, 60, false, encoder)!!)
        assertEquals(200, rotated.width)
        assertEquals(300, rotated.height)
    }

    @Test
    fun tecladoDoComputadorNoCelular() {
        val k = PhoneKeyboard()
        assertEquals(PhoneInput.Type("a"), k.key(0x41, false))
        assertNull(k.key(0x41, true))
        k.key(0x10, false)
        assertEquals(PhoneInput.Type("A"), k.key(0x41, false))
        assertEquals(PhoneInput.Type("@"), k.key(0x32, false))
        k.key(0x10, true)
        assertEquals(PhoneInput.Type("2"), k.key(0x32, false))
        assertEquals(PhoneInput.Do(PhoneAction.ENTER), k.key(0x0D, false))
        assertEquals(PhoneInput.Do(PhoneAction.BACK), k.key(0x1B, false))
        assertEquals(PhoneInput.Do(PhoneAction.BACK), k.key(PhoneKeys.BACK, false))
        assertEquals(PhoneInput.Do(PhoneAction.HOME), k.key(PhoneKeys.HOME, false))
        assertEquals(PhoneInput.Do(PhoneAction.RECENTS), k.key(PhoneKeys.RECENTS, false))
        k.key(0x11, false)
        assertEquals(PhoneInput.Do(PhoneAction.PASTE), k.key(0x56, false))
        assertNull(k.key(0x51, false))
        k.key(0x11, true)
        assertEquals(PhoneInput.Type("q"), k.key(0x51, false))
    }

    // ---------- sessão de ponta a ponta ----------

    private class Config(override val relayServer: String = "", var password: String? = null) : HostConfig {
        override val relayKey = ""
        override val hostName = "Celular de teste"
        override val version = "1.14.0"
        private val salt = ByteArray(16) { 3 }
        override val iterations = 1000
        override val passwordSalt: String get() = if (password == null) "" else Base64.getEncoder().encodeToString(salt)
        override val passwordVerifier: String
            get() = password?.let { Base64.getEncoder().encodeToString(HostCrypto.verifier(it, salt, iterations)) } ?: ""
        override var idleSeconds = 0
        val trusted: MutableMap<String, String> = Collections.synchronizedMap(HashMap())
        override fun isTrusted(keyHash: String) = trusted.containsKey(keyHash)
        override fun trust(keyHash: String, id: String, name: String, user: String, viaPassword: Boolean): Boolean {
            trusted[keyHash] = name
            return true
        }
        override fun touchTrusted(keyHash: String, name: String, user: String) = trusted.containsKey(keyHash)
        var key: HostKey? = null
        var account = true
        override val ticket: String get() = key?.takeIf { account }?.let { Ticket.signForTest("celular@teste", HostCrypto.keyHash(it.spki)) } ?: ""
    }

    private class FakePhone(var answer: Permission = Permission.ONCE) : HostPlatform {
        val inputs = ArrayBlockingQueue<String>(100)
        val asked = ArrayBlockingQueue<IncomingRequest>(10)
        val ended = CountDownLatch(1)
        @Volatile var width = 64
        @Volatile var height = 48
        private val builder = FrameBuilder(32)

        override fun askPermission(request: IncomingRequest): Permission {
            asked.add(request)
            return answer
        }

        override fun openSession(session: HostSessionInfo): SessionIo = object : SessionIo {
            override fun size() = width to height
            override fun frame(quality: Int, full: Boolean): ByteArray? {
                val pixels = IntArray(width * height) { 0xFF336699.toInt() }
                return builder.build(pixels, width, height, quality, full) { _, _, _, _, _ -> byteArrayOf(1, 2, 3) }
            }
            override fun mouse(x: Int, y: Int, action: Int, wheel: Int) { inputs.add("mouse $x $y $action $wheel") }
            override fun key(virtualKey: Int, up: Boolean) { inputs.add("key $virtualKey $up") }
            override fun text(text: String) { inputs.add("text $text") }
            override fun clipboard(text: String) { inputs.add("clip $text") }
            override fun problem(): String? = null
            override fun close() {}
        }

        override fun sessionEnded(session: HostSessionInfo) = ended.countDown()
    }

    private class Viewer : SessionListener {
        val frames = ArrayBlockingQueue<Frame>(100)
        val notices = ArrayBlockingQueue<String>(10)
        val closed = ArrayBlockingQueue<String>(1)
        override fun onFrame(frame: Frame) { frames.offer(frame) }
        override fun onClipboard(text: String) {}
        override fun onNotice(text: String) { notices.offer(text) }
        override fun onClosed(reason: String) { closed.offer(reason) }
    }

    private fun answers(password: String?) = object : ConnectCallbacks {
        override fun askPassword(): String? = password
        override fun confirmNewIdentity() = false
    }

    private val viewerIdentity: Identity = ecPair().let { EcIdentity(it.private, it.public) }
    private val viewerTicket = Ticket.signForTest("pc@teste", HostCrypto.keyHash(viewerIdentity.spki))
    private val viewerConfig = ClientConfig("", "", "PC do teste", "Teste", "1.14.0", viewerTicket)

    /** Um lugar onde o "celular" atende uma conexão direta, como faria pelo servidor. */
    private fun listen(host: PonteHost): ServerSocket {
        val server = ServerSocket(0)
        thread(isDaemon = true) {
            while (!server.isClosed) {
                val socket = try { server.accept() } catch (e: Exception) { return@thread }
                thread(isDaemon = true) { host.handle(socket, "teste") }
            }
        }
        return server
    }

    @Test
    fun entraComSenhaVeATelaEControla() {
        val config = Config(password = "s3nha çá")
        val phone = FakePhone()
        val host = PonteHost(config, newHostKey(config), phone)
        listen(host).use { server ->
            assertFailsWith<PonteException> {
                PonteClient.connect(viewerConfig, "127.0.0.1:${server.localPort}", viewerIdentity, null, false, answers("errada"))
            }.also { assertEquals("Senha incorreta.", it.message) }

            val session = PonteClient.connect(viewerConfig, "127.0.0.1:${server.localPort}", viewerIdentity, null, true, answers("s3nha çá"))
            assertEquals("Celular de teste", session.hostName)
            assertEquals(64, session.screens.single().width)
            assertTrue(session.paired, "pediu para lembrar e entrou com a senha: fica autorizado")
            assertTrue(session.isPhone)
            val viewer = Viewer()
            thread(isDaemon = true) { session.run(viewer) }
            val frame = viewer.frames.poll(5, TimeUnit.SECONDS)
            assertNotNull(frame)
            assertEquals(48, frame.height)

            session.sendMouse(100, 200, MouseAction.LEFT_DOWN)
            session.sendKey(0x41, false)
            session.sendText("olá 😀")
            assertEquals("mouse 100 200 1 0", phone.inputs.poll(5, TimeUnit.SECONDS))
            assertEquals("key 65 false", phone.inputs.poll(5, TimeUnit.SECONDS))
            assertEquals("text olá 😀", phone.inputs.poll(5, TimeUnit.SECONDS))

            // O celular girou: o quadro seguinte vem com o tamanho novo.
            phone.width = 48
            phone.height = 64
            var rotated: Frame? = null
            repeat(20) { if (rotated == null) rotated = viewer.frames.poll(1, TimeUnit.SECONDS)?.takeIf { it.width == 48 } }
            assertNotNull(rotated)

            session.close()
            assertTrue(phone.ended.await(5, TimeUnit.SECONDS))
            assertNull(host.session)
        }
    }

    @Test
    fun pedidoNaTelaESempreAutorizado() {
        val config = Config()
        val phone = FakePhone(answer = Permission.ALWAYS)
        val host = PonteHost(config, newHostKey(config), phone)
        listen(host).use { server ->
            val first = PonteClient.connect(viewerConfig, "127.0.0.1:${server.localPort}", viewerIdentity, null, false, answers(""))
            val request = phone.asked.poll(1, TimeUnit.SECONDS)!!
            assertEquals("PC do teste", request.name)
            assertEquals(Crypto.idFromSpki(viewerIdentity.spki), request.id)
            assertTrue(request.canRemember)
            assertTrue(first.paired)
            first.close()
            assertTrue(phone.ended.await(5, TimeUnit.SECONDS))

            // Da segunda vez entra direto, sem perguntar.
            phone.answer = Permission.REFUSE
            val second = PonteClient.connect(viewerConfig, "127.0.0.1:${server.localPort}", viewerIdentity, null, false, answers(""))
            assertTrue(second.paired)
            assertTrue(phone.asked.isEmpty())
            second.close()

            // Outro aparelho, recusado.
            val other = ecPair().let { EcIdentity(it.private, it.public) }
            assertFailsWith<PonteException> {
                PonteClient.connect(ClientConfig("", "", "Outro PC", "Teste", "1.17.0", Ticket.signForTest("outro@teste", HostCrypto.keyHash(other.spki))), "127.0.0.1:${server.localPort}", other, null, false, answers(""))
            }.also { assertEquals("O pedido foi recusado.", it.message) }
        }
    }

    @Test
    fun encerraPorFaltaDeUso() {
        val config = Config(password = "x")
        val phone = FakePhone()
        val host = PonteHost(config, newHostKey(config), phone)
        listen(host).use { server ->
            val session = PonteClient.connect(viewerConfig, "127.0.0.1:${server.localPort}", viewerIdentity, null, false, answers("x"))
            val viewer = Viewer()
            thread(isDaemon = true) { session.run(viewer) }
            session.sendSettings(0, 60, idleSeconds = 2)
            val reason = viewer.closed.poll(10, TimeUnit.SECONDS)
            assertEquals("Sessão encerrada por falta de uso: 2 segundos sem mexer no mouse, no teclado ou na tela.", reason)
        }
    }

    // ---------- servidor de retransmissão (simulado, com as regras do relay.js) ----------

    private class FakeRelay : AutoCloseable {
        val server = ServerSocket(0)
        val port get() = server.localPort
        private var control: Socket? = null
        @Volatile var registeredId: String? = null
        private val waiting = java.util.concurrent.ConcurrentHashMap<String, Socket>()
        val registered = CountDownLatch(1)

        init {
            thread(isDaemon = true) {
                while (!server.isClosed) {
                    val socket = try { server.accept() } catch (e: Exception) { return@thread }
                    thread(isDaemon = true) { serve(socket) }
                }
            }
        }

        private fun serve(socket: Socket) {
            val input = socket.getInputStream()
            val output = socket.getOutputStream()
            val parts = PonteHost.readLine(input).split(' ')
            when (parts[1]) {
                "HOST" -> {
                    val nonce = ByteArray(32) { it.toByte() }
                    PonteHost.writeLine(output, "NONCE " + nonce.joinToString("") { "%02x".format(it) })
                    val proof = PonteHost.readLine(input).split(' ')
                    val spki = Base64.getDecoder().decode(proof[1])
                    val ok = Crypto.idFromSpki(spki) == parts[2] && Signature.getInstance("SHA256withECDSA").run {
                        initVerify(KeyFactory.getInstance("EC").generatePublic(java.security.spec.X509EncodedKeySpec(spki)))
                        update(nonce)
                        verify(HostCrypto.p1363ToDer(Base64.getDecoder().decode(proof[2])))
                    }
                    if (!ok) { PonteHost.writeLine(output, "ERR proof"); socket.close(); return }
                    PonteHost.writeLine(output, "OK")
                    control = socket
                    registeredId = parts[2]
                    registered.countDown()
                    while (true) {
                        val line = try { PonteHost.readLine(input) } catch (e: Exception) { return }
                        if (line == "PING") synchronized(output) { PonteHost.writeLine(output, "PONG") }
                    }
                }
                "CONNECT" -> {
                    if (parts[2] != registeredId) { PonteHost.writeLine(output, "ERR offline"); socket.close(); return }
                    val token = "%032x".format(BigInteger(128, java.util.Random()))
                    waiting[token] = socket
                    val out = control!!.getOutputStream()
                    synchronized(out) { PonteHost.writeLine(out, "INCOMING $token") }
                }
                "ACCEPT" -> {
                    val viewer = waiting.remove(parts[2]) ?: run { PonteHost.writeLine(output, "ERR token"); socket.close(); return }
                    PonteHost.writeLine(viewer.getOutputStream(), "OK")
                    PonteHost.writeLine(output, "OK")
                    pipe(viewer.getInputStream(), output)
                    pipe(input, viewer.getOutputStream())
                }
            }
        }

        private fun pipe(from: InputStream, to: OutputStream) = thread(isDaemon = true) {
            val buffer = ByteArray(16 * 1024)
            try {
                while (true) {
                    val n = from.read(buffer)
                    if (n < 0) break
                    to.write(buffer, 0, n)
                    to.flush()
                }
            } catch (ignored: Exception) {
            } finally {
                try { to.close() } catch (ignored: Exception) { }
            }
        }

        override fun close() = server.close()
    }

    @Test
    fun registraOIdEAtendePeloServidor() {
        FakeRelay().use { relay ->
            val config = Config(relayServer = "127.0.0.1:${relay.port}", password = "abc")
            val phone = FakePhone()
            val key = newHostKey(config)
            val host = PonteHost(config, key, phone)
            host.start()
            try {
                assertTrue(relay.registered.await(5, TimeUnit.SECONDS))
                assertEquals(key.id, relay.registeredId)
                val client = ClientConfig("127.0.0.1:${relay.port}", "", "PC do teste", "Teste", "1.14.0", viewerTicket)
                val session = PonteClient.connect(client, key.id, viewerIdentity, null, false, answers("abc"))
                assertEquals("Celular de teste", session.hostName)
                val viewer = Viewer()
                thread(isDaemon = true) { session.run(viewer) }
                assertNotNull(viewer.frames.poll(5, TimeUnit.SECONDS))
                session.close()
            } finally {
                host.stop()
            }
        }
    }

    // ---------- conta obrigatória (1.17.0) ----------

    @Test
    fun semContaLiberadaNaoHaSessao() {
        val config = Config(password = "abc")
        val phone = FakePhone()
        val host = PonteHost(config, newHostKey(config), phone)
        listen(host).use { server ->
            val target = "127.0.0.1:${server.localPort}"
            // Quem acessa sem bilhete nem chega a tentar.
            val noTicket = ClientConfig("", "", "PC do teste", "Teste", "1.17.0")
            assertFailsWith<PonteException> { PonteClient.connect(noTicket, target, viewerIdentity, null, false, answers("abc")) }
                .also { assertTrue(it.message!!.contains("conta liberada"), it.message) }
            // Bilhete de outro aparelho não vale.
            val other = ecPair().let { EcIdentity(it.private, it.public) }
            assertFailsWith<PonteException> { PonteClient.connect(viewerConfig, target, other, null, false, answers("abc")) }
            // O celular sem conta recusa, e quem acessa também não aceita um celular sem conta.
            config.account = false
            assertFailsWith<PonteException> { PonteClient.connect(viewerConfig, target, viewerIdentity, null, false, answers("abc")) }
                .also { assertTrue(it.message!!.contains("conta liberada"), it.message) }
            assertTrue(phone.asked.isEmpty(), "sem conta, ninguém é perguntado na tela")
            config.account = true
            val session = PonteClient.connect(viewerConfig, target, viewerIdentity, null, false, answers("abc"))
            assertEquals("celular@teste", session.hostAccount)
            assertFalse(session.hasSecureAttention, "celular não tem Ctrl+Alt+Del")
            session.close()
        }
    }

    @Test
    fun bilheteSoValeAssinadoNoAparelhoDeleEDentroDaValidade() {
        val hash = HostCrypto.keyHash(viewerIdentity.spki)
        assertEquals("pc@teste", Ticket.verify(viewerTicket, hash).email)
        assertNull(Ticket.verify(viewerTicket, "00".repeat(32)).email)
        assertNull(Ticket.verify(Ticket.signForTest("a@b.c", hash, System.currentTimeMillis() / 1000 - 60), hash).email)
        val dot = viewerTicket.indexOf('.')
        val forged = Ticket.encode(Json.write(mapOf("v" to 1, "e" to "intruso@x.y", "k" to hash, "x" to 4102444800L)).toByteArray()) + viewerTicket.substring(dot)
        assertNull(Ticket.verify(forged, hash).email)
        val stranger = ecPair()
        val payload = Ticket.decode(viewerTicket.substring(0, dot))
        assertNull(Ticket.verify(viewerTicket.substring(0, dot) + "." + Ticket.encode(HostCrypto.sign(stranger.private, payload)), hash).email)
        assertNull(Ticket.verify("", hash).email)
        assertNull(Ticket.verify("lixo", hash).email)
        assertNull(Ticket.verify("a.b", hash).email)
        assertTrue(Ticket.valid(viewerTicket, hash, 3600))
        assertFalse(Ticket.valid(viewerTicket, hash, Ticket.RENEW_BEFORE_SECONDS), "bilhete de um dia já pede renovação")
    }

    // ---------- Ponte no navegador (o mesmo canal de WebChannel.cs, com o lado do navegador escrito aqui) ----------

    @Test
    fun navegadorConectaPeloCanalProprio() {
        val config = Config(password = "web")
        val phone = FakePhone()
        val key = newHostKey(config)
        val host = PonteHost(config, key, phone)
        listen(host).use { server ->
            Socket("127.0.0.1", server.localPort).use { socket ->
                val input = DataInputStream(socket.getInputStream())
                val output = socket.getOutputStream()
                val ephemeral = ecPair()
                val pub = ephemeral.public as ECPublicKey
                val point = byteArrayOf(4) + SelfSignedCertificate.fixed(pub.w.affineX) + SelfSignedCertificate.fixed(pub.w.affineY)
                val hello = WebSecure.MAGIC + point + ByteArray(32) { 5 }
                output.write(hello)
                output.flush()

                val serverPoint = ByteArray(65).also { input.readFully(it) }
                val certLength = input.readUnsignedShort()
                val cert = ByteArray(certLength).also { input.readFully(it) }
                val signature = ByteArray(64).also { input.readFully(it) }
                assertContentEquals(key.certificate, cert)
                val signed = "ponte-web-1\n".toByteArray() + Crypto.sha256(hello) + serverPoint + Crypto.sha256(cert)
                assertTrue(Signature.getInstance("SHA256withECDSA").run {
                    initVerify(SelfSignedCertificate.parse(cert).publicKey)
                    update(signed)
                    verify(HostCrypto.p1363ToDer(signature))
                })
                assertEquals(key.id, Crypto.idFromSpki(SelfSignedCertificate.parse(cert).publicKey.encoded))

                val serverKey = KeyFactory.getInstance("EC").generatePublic(ECPublicKeySpec(
                    ECPoint(BigInteger(1, serverPoint.copyOfRange(1, 33)), BigInteger(1, serverPoint.copyOfRange(33, 65))), pub.params))
                val secret = KeyAgreement.getInstance("ECDH").run { init(ephemeral.private); doPhase(serverKey, true); generateSecret() }
                val serverHello = serverPoint + byteArrayOf((certLength shr 8).toByte(), certLength.toByte()) + cert + signature
                val salt = Crypto.sha256(hello, serverHello)
                val c2s = WebSecure.hkdf(secret, salt, "ponte-web-1 c2s")
                val s2c = WebSecure.hkdf(secret, salt, "ponte-web-1 s2c")
                var sent = 0L
                var received = 0L
                fun nonce(counter: Long) = ByteArray(12).also { for (i in 0 until 8) it[4 + i] = (counter ushr (56 - 8 * i)).toByte() }
                fun send(type: Int, body: ByteArray) {
                    val plain = byteArrayOf(type.toByte(), body.size.toByte(), (body.size shr 8).toByte(), (body.size shr 16).toByte(), (body.size shr 24).toByte()) + body
                    val sealed = Cipher.getInstance("AES/GCM/NoPadding").run {
                        init(Cipher.ENCRYPT_MODE, SecretKeySpec(c2s, "AES"), GCMParameterSpec(128, nonce(sent++)))
                        doFinal(plain)
                    }
                    output.write(byteArrayOf((sealed.size ushr 24).toByte(), (sealed.size ushr 16).toByte(), (sealed.size ushr 8).toByte(), sealed.size.toByte()) + sealed)
                    output.flush()
                }
                fun receive(): Pair<Int, ByteArray> {
                    val length = input.readInt()
                    val sealed = ByteArray(length).also { input.readFully(it) }
                    val plain = Cipher.getInstance("AES/GCM/NoPadding").run {
                        init(Cipher.DECRYPT_MODE, SecretKeySpec(s2c, "AES"), GCMParameterSpec(128, nonce(received++)))
                        doFinal(sealed)
                    }
                    return (plain[0].toInt() and 0xFF) to plain.copyOfRange(5, plain.size)
                }

                // 1.17.0: o navegador também manda a chave dele e o bilhete da conta, e prova ser dono da chave.
                val browserKey = ecPair()
                val browserTicket = Ticket.signForTest("navegador@teste", HostCrypto.keyHash(browserKey.public.encoded))
                send(Msg.HELLO, Json.write(mapOf("Name" to "Navegador", "User" to "Web", "Version" to "1.17.0",
                    "Key" to Base64.getEncoder().encodeToString(browserKey.public.encoded), "Ticket" to browserTicket)).toByteArray())
                val (type, body) = receive()
                assertEquals(Msg.CHALLENGE, type)
                val challenge = Json.parseObject(String(body))
                assertTrue(challenge.bool("HasPassword"))
                val nonceBytes = Base64.getDecoder().decode(challenge.str("Nonce"))
                val verifier = Crypto.pbkdf2("web", Base64.getDecoder().decode(challenge.str("Salt")), challenge.int("Iterations"))
                val proof = Crypto.proof(verifier, nonceBytes, Crypto.sha256(cert))
                val pairing = HostCrypto.sign(browserKey.private, Crypto.pairingMessage(nonceBytes, Crypto.sha256(cert)))
                assertNotNull(Ticket.verify(challenge["Ticket"] as? String, HostCrypto.keyHash(key.spki)).email, "o celular manda o bilhete dele")
                send(Msg.AUTH_RESPONSE, Json.write(mapOf("Proof" to Base64.getEncoder().encodeToString(proof),
                    "Signature" to Base64.getEncoder().encodeToString(pairing))).toByteArray())
                val (resultType, resultBody) = receive()
                assertEquals(Msg.AUTH_RESULT, resultType)
                val result = Json.parseObject(String(resultBody))
                assertTrue(result.bool("Ok"), result.str("Reason"))
                assertEquals(Caps.PHONE, result.int("Caps"))
                // Depois da entrada, a tela chega pelo mesmo canal.
                var frameType = 0
                repeat(5) { if (frameType != Msg.FRAME) frameType = receive().first }
                assertEquals(Msg.FRAME, frameType)
                send(Msg.TEXT, "pelo navegador".toByteArray())
                assertEquals("text pelo navegador", phone.inputs.poll(5, TimeUnit.SECONDS))
            }
        }
    }
}
