# AnonimizadorDotNet

[![NuGet](https://img.shields.io/nuget/vpre/AnonimizadorDotNet.svg)](https://www.nuget.org/packages/AnonimizadorDotNet)
[![CI](https://github.com/Grogww/AnonimizadorDotNet/actions/workflows/ci.yml/badge.svg)](https://github.com/Grogww/AnonimizadorDotNet/actions/workflows/ci.yml)
[![Licença](https://img.shields.io/badge/licen%C3%A7a-Apache--2.0-blue.svg)](https://github.com/Grogww/AnonimizadorDotNet/blob/main/LICENSE)
![.NET 8+](https://img.shields.io/badge/.NET-8%2B-512BD4.svg)

Troca CPF, CNPJ, telefone, e-mail e nome por valores fictícios válidos e determinísticos, direto no
`DbDataReader`, sem nenhuma chamada de rede. O mesmo CPF vira sempre o mesmo CPF fictício, em qualquer tabela,
e os `JOIN`s continuam funcionando.

> **Prerelease (`0.x`).** A API ainda pode mudar entre versões.

## O problema

Mandar o resultado de uma consulta para um LLM, ou copiar a produção para uma base de teste, expõe dados
pessoais. As saídas comuns quebram os dados:

- **Máscara** (`***.***.***-**`): todos os CPFs ficam iguais, e não dá mais para relacionar registros.
- **Hash truncado** para caber no formato: gera colisões. Com 1 milhão de CPFs num espaço de 10⁹ números,
  são cerca de 500 pares de pessoas diferentes fundidas no mesmo CPF fictício.
- **Valor aleatório**: cada consulta devolve um CPF diferente para a mesma pessoa.

Esta biblioteca usa criptografia que preserva formato (FF1): cada CPF real tem um único CPF fictício, válido,
e dois CPFs diferentes nunca viram o mesmo.

## Antes e depois

| Tabela | Coluna | Original | Anonimizado |
|---|---|---|---|
| `Clientes` | `Cpf` | `529.982.247-25` | `680.133.296-15` |
| `Pedidos` | `CpfCliente` | `52998224725` | `68013329615` |
| `Clientes` | `Nome` | `Maria Souza` | `Raquel Couto` |
| `Clientes` | `Email` | `maria.souza@gmail.com` | `everton.lobato@gmail.com` |
| `Clientes` | `Telefone` | `(11) 98765-4321` | `(11) 98759-4010` |
| `Clientes` | `IdCliente` | `1` | `1` |

O CPF chega com máscara numa tabela e sem máscara na outra, e sai como o mesmo CPF fictício, cada um no seu
formato. A chave interna `IdCliente` passa intacta.

## Instalação

```bash
dotnet add package AnonimizadorDotNet --prerelease
```

Requer .NET 8 ou superior.

## Início rápido

```csharp
using AnonimizadorDotNet;

// Chave: Base64 de 32 bytes aleatórios (ex.: openssl rand -base64 32).
using var anonymizer = new AnonymizerBuilder()
    .UseKeyFromEnvironment("ANONIMIZADOR_CHAVE")
    .Build();

using var command = new SqlCommand("SELECT IdCliente, Nome, Cpf, Email FROM Clientes", connection);
using var reader = anonymizer.Wrap(command.ExecuteReader());

while (reader.Read())
    Console.WriteLine($"{reader["IdCliente"]} {reader["Nome"]} {reader["Cpf"]} {reader["Email"]}");
```

`Wrap` aceita qualquer `DbDataReader` (SQL Server, PostgreSQL, SQLite, MySQL…) e devolve outro
`DbDataReader`. Por isso ele funciona com ADO.NET, com o `ExecuteReader` do Dapper e com qualquer código que
leia um reader. As colunas são reconhecidas pelo nome. Nesse exemplo
não há nenhuma configuração.

## Como funciona

```
DbDataReader ─► classifica as colunas (uma vez por resultado) ─► transforma cada valor ─► DbDataReader
                regra explícita → chave interna → nome → varredura de texto → política
```

| Tipo | Técnica | Por quê |
|---|---|---|
| CPF, CNPJ, telefone | FF1 (NIST SP 800-38G) sobre AES; dígitos verificadores recalculados | É uma permutação: sem colisão, então as junções entre tabelas continuam corretas |
| CNPJ | Só a raiz (8 posições) é cifrada; a ordem (`0001`) é preservada | Matriz e filiais continuam com a mesma raiz. O CNPJ alfanumérico continua alfanumérico |
| Nome, e-mail | HMAC-SHA256 escolhendo entradas de dicionários embutidos | Não servem de chave de junção, então uma colisão rara é aceitável |
| Chaves internas | Passam intactas (`Id`, `IdCliente`, PKs e FKs inteiras, GUIDs) | São elas que mantêm as relações do banco |
| Texto livre | Varredura de CPF, CNPJ, e-mail e telefone com DDD | Só troca trechos com verificadores válidos; datas, valores e CEP ficam |

O formato de cada valor é reaplicado: com ou sem máscara, texto ou número, com os zeros à esquerda.

## Exemplos

A saída mostrada vem da execução real dos exemplos, com uma chave fixa de teste.

### Valor individual

```csharp
anonymizer.TryAnonymize("529.982.247-25", PersonalDataTypes.Cpf, out var a);    // 680.133.296-15
anonymizer.TryAnonymize("52998224725", PersonalDataTypes.Cpf, out var b);       // 68013329615
anonymizer.TryAnonymize("11.222.333/0001-81", PersonalDataTypes.Cnpj, out var c); // 34.410.014/0001-03
anonymizer.TryAnonymize("123.456.789-00", PersonalDataTypes.Cpf, out _);       // false: DV inválido
```

### Texto livre

```csharp
anonymizer.AnonymizeText(
    "Falar com Maria, CPF 529.982.247-25, maria.souza@gmail.com, tel (11) 98765-4321. " +
    "Vencimento 10/12/2026, valor R$ 1.234,56.");
```

```
Falar com Maria, CPF 680.133.296-15, everton.lobato@gmail.com, tel (11) 98759-4010.
Vencimento 10/12/2026, valor R$ 1.234,56.
```

O nome "Maria" no meio do texto **não** é trocado: a varredura não encontra nomes nem endereços.

### Configuração explícita

Quando o nome da coluna não diz o que ela guarda:

```csharp
using var anonymizer = new AnonymizerBuilder()
    .UseKeyFromEnvironment("ANONIMIZADOR_CHAVE")
    .TransformColumn("doc", PersonalDataTypes.Cpf)               // ou tabela.coluna, schema.tabela.coluna
    .ScanColumn("obs")                                           // texto livre
    .IgnoreColumn("codigo_interno")                              // passa intacta
    .WithUnclassifiedPolicy(UnclassifiedPolicy.Block)            // o resto sai como DBNull
    .Build();
```

| Id | doc | obs | codigo_interno |
|---|---|---|---|
| `1` | `52998224725` → `68013329615` | `Cliente 111.444.777-35 ligou` → `Cliente 645.674.055-01 ligou` | `ABC-123` |

Com `UnclassifiedPolicy.Pass` (o padrão), as colunas de texto não reconhecidas são varridas e as demais
passam. Com `Block`, só saem as chaves internas, as colunas classificadas e as configuradas; o resto sai
como `DBNull`.

### Tipo próprio

Um transformador e, se quiser encontrar o tipo em texto livre, um detector. Exemplo com placa de veículo:

```csharp
sealed class PlacaTransformer(byte[] key) : IValueTransformer
{
    public string Type => "placa";

    public bool TryTransform(ReadOnlySpan<char> value, ValueKind kind,
        [NotNullWhen(true)] out string? substitute)
    {
        substitute = null;
        if (value.Length != 7) return false;

        var hash = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(value.ToString()));
        var chars = new char[7];
        for (var i = 0; i < 7; i++)
            chars[i] = i is 3 or 5 or 6 ? (char)('0' + hash[i] % 10) : (char)('A' + hash[i] % 26);
        substitute = new string(chars);
        return true;
    }
}

sealed class PlacaDetector : ITextDetector
{
    public string Type => "placa";

    public IEnumerable<TextMatch> Detect(string text) =>
        Regex.Matches(text, @"\b[A-Z]{3}\d[A-Z0-9]\d{2}\b").Select(m => new TextMatch(m.Index, m.Length));
}

using var anonymizer = new AnonymizerBuilder()
    .UseKeyFromEnvironment("ANONIMIZADOR_CHAVE")
    .AddTransformer(new PlacaTransformer(placaKey))
    .AddDetector(new PlacaDetector())
    .TransformColumn("Veiculos.placa", "placa")
    .Build();

anonymizer.AnonymizeText("Veículo ABC1D23 estacionado; outro: BRA2E19.");
// Veículo EMM6B58 estacionado; outro: NPV7P26.
```

### Anonimizar uma base de teste

Lê pelo reader anonimizado e grava de volta (exemplo com SQLite):

```csharp
using var anonymizer = new AnonymizerBuilder().UseKeyFromEnvironment("ANONIMIZADOR_CHAVE").Build();

var rows = new List<(long Id, string Nome, string Cpf, string Email)>();
using (var select = new SqliteCommand("SELECT Id, Nome, Cpf, Email FROM Clientes", db))
using (var reader = anonymizer.Wrap(select.ExecuteReader()))
    while (reader.Read())
        rows.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));

using var tx = db.BeginTransaction();
using var update = new SqliteCommand(
    "UPDATE Clientes SET Nome = $nome, Cpf = $cpf, Email = $email WHERE Id = $id", db, tx);
foreach (var row in rows)
{
    update.Parameters.Clear();
    update.Parameters.AddWithValue("$id", row.Id);
    update.Parameters.AddWithValue("$nome", row.Nome);
    update.Parameters.AddWithValue("$cpf", row.Cpf);
    update.Parameters.AddWithValue("$email", row.Email);
    update.ExecuteNonQuery();
}
tx.Commit();
```

Como o resultado é determinístico, rodar o script em cada tabela mantém os CPFs coerentes entre elas.

## Casos de uso

- **MCP SQL somente leitura para LLMs:** o servidor MCP envolve o reader antes de devolver o resultado. O LLM
  vê dados com formato real, faz `JOIN`s e agrupa por cliente sem ver um único CPF verdadeiro.
- **Base de teste e homologação:** uma cópia da produção com dados pessoais trocados e relações intactas.
- **Suporte e análise de dados:** consultas e exportações para quem precisa dos padrões, não das pessoas.
- **Desenvolvimento local:** dados realistas para depurar sem levar dado pessoal para a máquina do
  desenvolvedor.

## Garantias e limites

| Garante | Não faz |
|---|---|
| Mesmo valor + mesma chave → mesmo substituto, entre tabelas, consultas e execuções | Não restaura os valores originais (planejado para a v1.x) |
| CPF, CNPJ e telefone sem colisão (permutação) | Não encontra nomes nem endereços em texto livre |
| Substitutos passam na validação de DV e mantêm o formato | Não trata valor inválido em coluna classificada: ele **passa como está** |
| Chaves internas, metadados e tipos CLR intactos | Não reconhece pelo nome uma coluna renomeada por alias quando o provedor não informa `BaseColumnName` |
| Nenhuma chamada de rede; sem dependências além da BCL | Não lê JSON nem outros formatos além de `DbDataReader` e texto |
| `Anonymizer` seguro para várias threads | O reader devolvido, como qualquer reader, não é |

Desempenho aproximado (BenchmarkDotNet, job curto, .NET 8, Intel Core i5-13450HX):

| Operação | Tempo |
|---|---|
| CPF, CNPJ ou telefone (FF1), por valor | ~9 µs (~110 mil valores/s por thread) |
| Nome ou e-mail (HMAC + dicionário), por valor | ~5 µs |
| CPF em paralelo (16 threads lógicas), por valor | ~3,4 µs |
| Varredura de texto livre com documentos | ~130 µs por KB |

## Segurança e LGPD

- **É pseudonimização, não anonimização** (LGPD, art. 13). Quem tem a chave consegue reverter CPF, CNPJ e
  telefone. Guarde a chave como um segredo e fora do repositório.
- **Trocar a chave muda todos os substitutos.** Para manter coerência entre execuções, use sempre a mesma.
- **Um CPF ou CNPJ fictício pode coincidir com o de uma pessoa real:** ele é válido e foi escolhido no
  mesmo espaço de números. Não use os substitutos para nenhuma ação fora do ambiente anonimizado.
- **No caso MCP, o LLM não deve filtrar por valores fictícios** (um `WHERE Cpf = ...` com o CPF que ele viu
  não encontra nada). Ele deve navegar pelas chaves internas, que passam intactas.
- **Um CPF digitado com erro na base não é trocado**, porque não é um CPF válido. Ele chega ao destino como
  está.

A biblioteca reduz riscos, mas não substitui uma avaliação jurídica sobre o tratamento de dados pessoais.

## Documentação

- [Escopo](https://github.com/Grogww/AnonimizadorDotNet/blob/main/docs/escopo.md): propósito, decisões e
  funcionamento.
- [Arquitetura](https://github.com/Grogww/AnonimizadorDotNet/blob/main/docs/arquitetura.md): organização da
  solução e das camadas.
- [Backlog](https://github.com/Grogww/AnonimizadorDotNet/blob/main/docs/backlog.md): melhorias previstas
  depois da v1.

## Licença

Distribuído sob a [Apache License 2.0](https://github.com/Grogww/AnonimizadorDotNet/blob/main/LICENSE).
