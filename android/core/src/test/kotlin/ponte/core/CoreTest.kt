package ponte.core

import java.security.KeyPairGenerator
import java.security.Signature
import java.security.spec.ECGenParameterSpec
import kotlin.test.Test
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertTrue

/** Os valores de referência vieram de outra implementação (hashlib do Python), não deste código. */
class CoreTest {
    private fun hex(bytes: ByteArray) = bytes.joinToString("") { "%02x".format(it) }

    @Test
    fun idSaiDaChavePublica() {
        assertEquals("817984555", Crypto.idFromSpki("ponte".toByteArray()))
        assertEquals("307488558", Crypto.idFromSpki(ByteArray(91) { it.toByte() }))
        assertEquals("817 984 555", Crypto.formatId("817984555"))
    }

    @Test
    fun pbkdf2ConfereComVetoresConhecidos() {
        val salt = "salt".toByteArray()
        assertEquals("120fb6cffcf8b32c43e7225256c4f837a86548c92ccc35480805987cb70be17b", hex(Crypto.pbkdf2("password", salt, 1)))
        assertEquals("ae4d0c95af6b46d32d0adff928f06dd02a303f8ef3c251dfd6e2d85a95474c43", hex(Crypto.pbkdf2("password", salt, 2)))
        assertEquals("c5e478d59288c841aa530db6845c4c8d962893a001ce4e11a4963873aa98134a", hex(Crypto.pbkdf2("password", salt, 4096)))
        // Senha com acento e símbolo: precisa entrar em UTF-8, como no programa do Windows.
        assertEquals(
            "e203a32275ad887c1d47a7d83e8727963e26f11dcfb02b27daa0dd8b29bd6733",
            hex(Crypto.pbkdf2("ação€", ByteArray(16) { it.toByte() }, 1000)),
        )
    }

    @Test
    fun provaDaSenha() {
        val verifier = Crypto.pbkdf2("password", "salt".toByteArray(), 2)
        assertEquals(
            "395ac04ed09a60394468f3dca26dc27f30e64e330014121b3fc630abe8d92938",
            hex(Crypto.proof(verifier, ByteArray(32), ByteArray(32) { 7 })),
        )
    }

    @Test
    fun assinaturaSaiNoFormatoQueODotNetConfere() {
        val pair = KeyPairGenerator.getInstance("EC").apply { initialize(ECGenParameterSpec("secp256r1")) }.generateKeyPair()
        val identity = EcIdentity(pair.private, pair.public)
        assertEquals(91, identity.spki.size)
        repeat(50) { round ->
            val message = Crypto.pairingMessage(ByteArray(32) { (it + round).toByte() }, ByteArray(32) { 3 })
            val signature = identity.sign(message)
            assertEquals(64, signature.size)
            val verifier = Signature.getInstance("SHA256withECDSAinP1363Format")
            verifier.initVerify(pair.public)
            verifier.update(message)
            assertTrue(verifier.verify(signature), "assinatura $round")
        }
    }

    @Test
    fun conversaoDeAssinaturaCompletaComZeros() {
        // r com zero à esquerda sobrando (33 bytes) e s curto (1 byte).
        val der = byteArrayOf(0x30, 0x26, 0x02, 0x21, 0x00) + ByteArray(32) { 0xFF.toByte() } + byteArrayOf(0x02, 0x01, 0x05)
        val out = Crypto.derToP1363(der)
        assertContentEquals(ByteArray(32) { 0xFF.toByte() }, out.copyOfRange(0, 32))
        assertContentEquals(ByteArray(31) + byteArrayOf(5), out.copyOfRange(32, 64))
        assertFailsWith<IllegalArgumentException> { Crypto.derToP1363(byteArrayOf(0x31, 0x00)) }
    }

    @Test
    fun jsonVaiEVolta() {
        val text = Json.write(mapOf("Name" to "Celular \"A\"\n", "Proof" to null, "Remember" to true, "N" to 42, "L" to listOf(1, "x")))
        assertEquals("""{"Name":"Celular \"A\"\n","Proof":null,"Remember":true,"N":42,"L":[1,"x"]}""", text)
        val back = Json.parseObject(text)
        assertEquals("Celular \"A\"\n", back.str("Name"))
        assertEquals(null, back["Proof"])
        assertTrue(back.bool("Remember"))
        assertEquals(42, back.int("N"))
    }

    @Test
    fun jsonLeOQueODotNetEscreve() {
        // O System.Text.Json escapa o que não é ASCII e alguns símbolos, como o + em base64.
        val o = Json.parseObject("""{"Ok":true,"Reason":"Senha incorreta é 😀","HostName":"PC","Screens":[{"Index":0,"Width":1920,"Height":1080,"Primary":true}],"Nonce":"ab+c","Paired":false}""")
        assertTrue(o.bool("Ok"))
        assertEquals("Senha incorreta é 😀", o.str("Reason"))
        assertEquals("ab+c", o.str("Nonce"))
        assertEquals(1920, o.objects("Screens").single().int("Width"))
        // Campo que uma versão antiga não manda vale o padrão.
        assertFalse(o.bool("Trusted"))
        assertEquals("", o.str("Version"))
        assertFailsWith<IllegalArgumentException> { Json.parse("""{"a":1""") }
        assertFailsWith<IllegalArgumentException> { Json.parse("""{"a":1} x""") }
    }

    @Test
    fun destinoPorIdOuEndereco() {
        Target.parse(" 460 087-520 ").let {
            assertTrue(it.isId)
            assertEquals("460087520", it.id)
            assertEquals("id:460087520", it.key)
            assertEquals("460 087 520", it.shown)
        }
        Target.parse("192.168.0.50").let {
            assertFalse(it.isId)
            assertEquals("192.168.0.50", it.host)
            assertEquals(7411, it.port)
            assertEquals("ip:192.168.0.50", it.key)
        }
        Target.parse("Servidor:9000").let {
            assertEquals("Servidor", it.host)
            assertEquals(9000, it.port)
            assertEquals("ip:servidor:9000", it.key)
        }
        // Dez dígitos não são um ID.
        assertFalse(Target.parse("1234567890").isId)
    }

    @Test
    fun quadroDeImagem() {
        fun u16(v: Int) = byteArrayOf(v.toByte(), (v shr 8).toByte())
        val jpeg = byteArrayOf(1, 2, 3)
        val payload = u16(1920) + u16(1080) + u16(1) + u16(64) + u16(128) + u16(32) + u16(16) + byteArrayOf(3, 0, 0, 0) + jpeg
        val frame = Session.parseFrame(payload)
        assertEquals(1920, frame.width)
        assertEquals(1080, frame.height)
        val tile = frame.tiles.single()
        assertEquals(listOf(64, 128, 32, 16, 3), listOf(tile.x, tile.y, tile.width, tile.height, tile.length))
        assertContentEquals(jpeg, tile.data.copyOfRange(tile.offset, tile.offset + tile.length))
        // Retângulo fora da tela ou tamanho maior que o conteúdo derruba o quadro.
        assertFailsWith<PonteException> { Session.parseFrame(u16(100) + u16(100) + u16(1) + u16(90) + u16(0) + u16(32) + u16(16) + byteArrayOf(3, 0, 0, 0) + jpeg) }
        assertFailsWith<PonteException> { Session.parseFrame(u16(1920) + u16(1080) + u16(1) + u16(0) + u16(0) + u16(32) + u16(16) + byteArrayOf(9, 0, 0, 0) + jpeg) }
    }

    private fun bytes(hex: String) = ByteArray(hex.length / 2) { hex.substring(it * 2, it * 2 + 2).toInt(16).toByte() }

    // Os mesmos bytes e as mesmas amostras estão no autoteste do Windows (SelfTest.cs): se as duas
    // implementações conferem com este vetor, uma entende o que a outra produz.
    @Test
    fun somComprimidoConfereComOVetorDeReferencia() {
        val sound = AudioCodec.decode(bytes("020280BB0000000000000000000000F7F7F7F7F7777F7F7FD588"))!!
        assertEquals(48000, sound.rate)
        assertEquals(2, sound.channels)
        assertEquals(
            listOf(0, 0, 11, -11, 41, -41, 104, -104, 240, -240, 533, -533, 1164, 98, -193, 1455, -3103, 4365, -9339, 10601, 467, 795, -838, -510),
            sound.samples.map { it.toInt() },
        )
        // Um pedaço do meio de uma sessão: o estado de cada canal vem na mensagem, e não parte do zero.
        val middle = AudioCodec.decode(bytes("020244AC00002EFB280029094D008784"))!!
        assertEquals(44100, middle.rate)
        assertEquals(listOf(-603, 910, 211, -395), middle.samples.map { it.toInt() })
    }

    @Test
    fun somSemCompressao() {
        // Formato 1, dois canais, 48000 Hz; depois 1, -2, 32767 e -32768 em 16 bits.
        val sound = AudioCodec.decode(bytes("010280BB0000" + "0100" + "FEFF" + "FF7F" + "0080"))!!
        assertEquals(listOf(1, -2, 32767, -32768), sound.samples.map { it.toInt() })
        // Um byte sobrando no fim não vira amostra.
        assertEquals(2, AudioCodec.decode(bytes("010280BB0000" + "0100" + "FEFF" + "FF"))!!.samples.size)
        // Formato desconhecido, taxa absurda e mensagem curta demais são descartados.
        assertEquals(null, AudioCodec.decode(bytes("070280BB00000100FEFF")))
        assertEquals(null, AudioCodec.decode(bytes("010200000000" + "0100FEFF")))
        assertEquals(null, AudioCodec.decode(bytes("0102")))
        // ADPCM só existe em dois canais.
        assertEquals(null, AudioCodec.decode(bytes("020180BB0000000000000000000000F7")))
    }

    @Test
    fun ponteiroDoMouse() {
        val png = byteArrayOf(9, 8, 7, 6)
        val shape = Session.parsePointer(byteArrayOf(1, 3, 0, 0x10, 0x01) + png)!!
        assertEquals(3, shape.hotX)
        assertEquals(272, shape.hotY)
        assertContentEquals(png, shape.data.copyOfRange(shape.offset, shape.offset + shape.length))
        // Escondido, ou visível mas sem imagem: não há o que desenhar.
        assertEquals(null, Session.parsePointer(byteArrayOf(0, 0, 0, 0, 0)))
        assertEquals(null, Session.parsePointer(byteArrayOf(1, 0, 0, 0, 0)))
    }

    @Test
    fun posicaoDoPonteiro() {
        assertContentEquals(intArrayOf(258, 65535, 1), Session.parsePointerPosition(byteArrayOf(1, 2, 1, -1, -1)))
        assertContentEquals(intArrayOf(0, 0, 0), Session.parsePointerPosition(byteArrayOf(0, 0, 0, 0, 0)))
        assertEquals(null, Session.parsePointerPosition(byteArrayOf(1, 2, 3)))
    }

    @Test
    fun versaoDoComputador() {
        assertTrue(Session.versionAtLeast("1.3.0", 1, 3))
        assertTrue(Session.versionAtLeast("2.0.1", 1, 3))
        assertFalse(Session.versionAtLeast("1.2.1", 1, 3))
        assertFalse(Session.versionAtLeast("", 1, 3))
    }
}
