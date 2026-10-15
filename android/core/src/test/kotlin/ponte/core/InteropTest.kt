package ponte.core

import java.io.ByteArrayInputStream
import java.security.KeyPairGenerator
import java.security.spec.ECGenParameterSpec
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import javax.imageio.ImageIO
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertTrue
import org.junit.jupiter.api.Assumptions.assumeTrue

/**
 * Conecta num Ponte do Windows de verdade. Só roda quando o destino vem por variável de ambiente:
 *
 *   PONTE_TESTE_ALVO    endereço direto, por exemplo 127.0.0.1:17420
 *   PONTE_TESTE_SENHA   senha de acesso configurada lá
 *   PONTE_TESTE_ID      (opcional) o ID de lá, para repetir o caminho pelo servidor de retransmissão
 *   PONTE_TESTE_RELAY, PONTE_TESTE_CHAVE   servidor e chave, quando PONTE_TESTE_ID é usado
 *
 * O computador testado mostra o aviso de sessão ativa durante o teste. Nada é digitado nem clicado lá
 * além de uma tecla F24, que não existe em teclado comum.
 */
class InteropTest {
    private val alvo = System.getenv("PONTE_TESTE_ALVO") ?: ""
    private val senha = System.getenv("PONTE_TESTE_SENHA") ?: ""
    private val id = System.getenv("PONTE_TESTE_ID") ?: ""
    private val config = ClientConfig(System.getenv("PONTE_TESTE_RELAY") ?: "", System.getenv("PONTE_TESTE_CHAVE") ?: "", "teste-kotlin", "gradle", "0.0.0")

    private fun novaIdentidade(): Identity {
        val pair = KeyPairGenerator.getInstance("EC").apply { initialize(ECGenParameterSpec("secp256r1")) }.generateKeyPair()
        return EcIdentity(pair.private, pair.public)
    }

    private fun respostas(password: String?, asked: BooleanArray? = null) = object : ConnectCallbacks {
        override fun askPassword(): String? {
            asked?.set(0, true)
            return password
        }

        override fun confirmNewIdentity() = false
    }

    private class Coleta : SessionListener {
        val firstFrame = CountDownLatch(1)
        val firstPointer = CountDownLatch(1)
        @Volatile var pointer: PointerShape? = null
        val secondFull = CountDownLatch(2)
        val closed = CountDownLatch(1)
        @Volatile var frame: Frame? = null
        @Volatile var reason: String? = null

        override fun onFrame(frame: Frame) {
            if (this.frame == null) this.frame = frame
            firstFrame.countDown()
            // Uma tela inteira num retângulo só: o primeiro quadro e o que vem depois de trocar a qualidade.
            if (frame.tiles.size == 1 && frame.tiles[0].width == frame.width && frame.tiles[0].height == frame.height) secondFull.countDown()
        }

        override fun onPointer(shape: PointerShape?) {
            pointer = shape
            firstPointer.countDown()
        }

        override fun onClipboard(text: String) {}
        override fun onNotice(text: String) {}
        override fun onClosed(reason: String) {
            this.reason = reason
            closed.countDown()
        }
    }

    @Test
    fun conectaDiretoComSenha() {
        assumeTrue(alvo.isNotEmpty(), "sem PONTE_TESTE_ALVO: teste de interoperação pulado")
        val identity = novaIdentidade()

        val errada = assertFailsWith<PonteException> { PonteClient.connect(config, alvo, identity, null, false, respostas("senha-errada")) }
        assertEquals("Senha incorreta.", errada.message)

        val session = PonteClient.connect(config, alvo, identity, null, false, respostas(senha))
        println("conectado a ${session.hostName}, Ponte ${session.hostVersion}, telas ${session.screens.map { "${it.width}x${it.height}" }}")
        assertTrue(session.hostName.isNotEmpty())
        assertTrue(session.screens.isNotEmpty())
        assertFalse(session.paired, "sem pedir, o aparelho não pode ficar autorizado")
        assertEquals(64, session.fingerprint.length)
        assertTrue(session.acceptsText, "o computador testado deveria ser 1.3.0 ou mais novo")

        val coleta = Coleta()
        val reader = Thread { session.run(coleta) }.apply { start() }
        assertTrue(coleta.firstFrame.await(15, TimeUnit.SECONDS), "a primeira imagem não chegou")
        val frame = assertNotNull(coleta.frame)
        val tile = frame.tiles.first()
        val image = assertNotNull(ImageIO.read(ByteArrayInputStream(tile.data, tile.offset, tile.length)), "o retângulo não é um JPEG legível")
        println("primeira imagem: ${frame.width}x${frame.height}, ${frame.tiles.size} retângulo(s), JPEG de ${tile.length} bytes")
        assertEquals(tile.width, image.width)
        assertEquals(tile.height, image.height)
        assertEquals(session.screens.first { it.primary }.width, frame.width)

        // O Windows 1.4 envia a forma do cursor separada da imagem; conferir o PNG e o ponto ativo.
        if (Session.versionAtLeast(session.hostVersion, 1, 5)) {
            assertTrue(coleta.firstPointer.await(5, TimeUnit.SECONDS), "o ponteiro não chegou")
            coleta.pointer?.let { shape ->
                val cursor = assertNotNull(ImageIO.read(ByteArrayInputStream(shape.data, shape.offset, shape.length)))
                assertTrue(shape.hotX in 0 until cursor.width)
                assertTrue(shape.hotY in 0 until cursor.height)
            }
            assertTrue(session.hasSound, "o perfil de teste deve permitir som")
        }

        // Trocar a qualidade faz o outro lado mandar a tela inteira de novo: prova que as mensagens enviadas chegam.
        session.sendSettings(session.screens.first { it.primary }.index, 35)
        session.sendKey(0x87, false)
        session.sendKey(0x87, true)
        assertTrue(coleta.secondFull.await(15, TimeUnit.SECONDS), "a tela inteira não voltou depois de trocar a qualidade")

        session.close()
        assertTrue(coleta.closed.await(10, TimeUnit.SECONDS), "a sessão não terminou")
        assertEquals("", coleta.reason)
        reader.join(5000)

        // A identidade vista agora tem de valer na conexão seguinte; uma diferente tem de ser percebida.
        val recusada = assertFailsWith<PonteException> { PonteClient.connect(config, alvo, identity, "00".repeat(32), false, respostas(senha)) }
        assertEquals("Conexão cancelada.", recusada.message)
    }

    @Test
    fun conectaPeloServidorEFicaAutorizado() {
        assumeTrue(alvo.isNotEmpty() && id.isNotEmpty(), "sem PONTE_TESTE_ID: caminho pelo servidor pulado")
        val identity = novaIdentidade()

        // Um ID que não é o da chave apresentada tem de ser recusado: aqui, o ID certo com um dígito trocado
        // nem está registrado no servidor.
        val outro = (if (id.last() == '0') id.dropLast(1) + "1" else id.dropLast(1) + "0")
        assertFailsWith<PonteException> { PonteClient.connect(config, outro, identity, null, false, respostas(senha)) }

        // Entrar com a senha pedindo para ficar autorizado.
        val first = PonteClient.connect(config, id, identity, null, true, respostas(senha))
        assertTrue(first.paired, "entrar com a senha pedindo autorização deveria autorizar")
        val fingerprint = first.fingerprint
        encerra(first)

        // Agora sem senha: se ela for pedida, o teste desiste e falha.
        val asked = BooleanArray(1)
        val direct = PonteClient.connect(config, id, identity, fingerprint, false, respostas(null, asked))
        assertFalse(asked[0], "aparelho autorizado não deveria ser perguntado pela senha")
        assertTrue(direct.paired)
        encerra(direct)

        // Outra chave não herda a autorização: volta a ser perguntada pela senha.
        val askedStranger = BooleanArray(1)
        assertFailsWith<PonteException> { PonteClient.connect(config, id, novaIdentidade(), fingerprint, false, respostas(null, askedStranger)) }
        assertTrue(askedStranger[0])
    }

    private fun encerra(session: Session) {
        val coleta = Coleta()
        val reader = Thread { session.run(coleta) }.apply { start() }
        coleta.firstFrame.await(10, TimeUnit.SECONDS)
        session.close()
        coleta.closed.await(10, TimeUnit.SECONDS)
        reader.join(5000)
        // O outro lado atende uma sessão por vez; dá tempo de ele liberar.
        Thread.sleep(800)
    }
}
