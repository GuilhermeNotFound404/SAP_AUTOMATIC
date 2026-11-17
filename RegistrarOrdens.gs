function doPost(e) {
  const body = e && e.postData;
  if (!body || typeof body.contents !== 'string' || typeof body.type !== 'string' ||
      !/^application\/json(?:\s*;|$)/i.test(body.type) || body.contents.length > 8192) {
    return responder_({ ok: false, erro: 'Envie um objeto JSON.' });
  }

  let dados;
  try {
    dados = JSON.parse(body.contents);
  } catch (_) {
    return responder_({ ok: false, erro: 'JSON inválido.' });
  }
  if (!dados || typeof dados !== 'object' || Array.isArray(dados)) {
    return responder_({ ok: false, erro: 'Envie um objeto JSON.' });
  }

  try {
    const propriedades = PropertiesService.getScriptProperties();
    const chave = propriedades.getProperty('CHAVE_PLANILHA');
    if (!chavesIguais_(chave, dados.chave)) {
      return responder_({ ok: false, erro: 'Acesso negado.' });
    }

    const ordem = typeof dados.ordem === 'string' ? dados.ordem.trim() : '';
    const identificador = normalizarOrdem_(ordem);
    const data = serialData_(dados.dataAbertura);
    const campos = [texto_(dados.frota, 50), texto_(dados.empresa, 120), texto_(dados.status, 60),
      texto_(dados.tipo, 80), texto_(dados.nome, 250)];
    if (!identificador || data === null || campos.some(campo => campo === null)) {
      return responder_({ ok: false, erro: 'Informe ordem, data de abertura, frota, empresa, status, tipo e nome válidos.' });
    }

    const planilhaId = propriedades.getProperty('ID_PLANILHA');
    if (!planilhaId || !/^[\w-]{15,160}$/.test(planilhaId)) {
      return responder_({ ok: false, erro: 'Configure o ID da planilha no Apps Script.' });
    }
    const nomeAba = propriedades.getProperty('ABA_ORDENS') || 'Ordens';
    const trava = LockService.getScriptLock();
    if (!trava.tryLock(10000)) {
      return responder_({ ok: false, erro: 'Envio ocupado. Tente novamente.' });
    }

    try {
      const aba = SpreadsheetApp.openById(planilhaId).getSheetByName(nomeAba);
      if (!aba || aba.getMaxColumns() < 7) {
        return responder_({ ok: false, erro: 'A aba configurada precisa existir e ter pelo menos sete colunas.' });
      }
      const ultimaLinha = aba.getLastRow();
      if (ultimaLinha > 0 && aba.getRange(1, 1, ultimaLinha, 1).getValues()
        .some(([valor]) => normalizarOrdem_(valor) === identificador)) {
        return responder_({ ok: true, ordem, duplicado: true });
      }

      const dataCelula = {
        userEnteredValue: { numberValue: data },
        userEnteredFormat: { numberFormat: { type: 'DATE', pattern: 'dd/MM/yyyy' } }
      };
      // Valor tipado preserva os zeros à esquerda e impede interpretar texto como fórmula.
      const linha = [celulaTexto_(ordem), dataCelula, ...campos.map(celulaTexto_)];
      Sheets.Spreadsheets.batchUpdate({
        requests: [{ appendCells: {
          sheetId: aba.getSheetId(),
          rows: [{ values: linha }],
          fields: 'userEnteredValue,userEnteredFormat.numberFormat'
        } }]
      }, planilhaId);
      SpreadsheetApp.flush();
      return responder_({ ok: true, ordem, duplicado: false });
    } finally {
      trava.releaseLock();
    }
  } catch (_) {
    return responder_({ ok: false, erro: 'Falha ao acessar a planilha. Confira a configuração, as permissões e o serviço Google Sheets.' });
  }
}

function responder_(resultado) {
  return ContentService.createTextOutput(JSON.stringify(resultado)).setMimeType(ContentService.MimeType.JSON);
}

function chavesIguais_(esperada, fornecida) {
  if (typeof esperada !== 'string' || esperada.length < 32 || esperada.length > 256 ||
      typeof fornecida !== 'string' || fornecida.length < 32 || fornecida.length > 256) {
    return false;
  }
  const hash = valor => Utilities.computeDigest(Utilities.DigestAlgorithm.SHA_256, valor, Utilities.Charset.UTF_8);
  const hashes = [hash(esperada), hash(fornecida)];
  let diferenca = 0;
  for (let i = 0; i < hashes[0].length; i++) {
    diferenca |= hashes[0][i] ^ hashes[1][i];
  }
  return diferenca === 0;
}

function normalizarOrdem_(valor) {
  if (typeof valor !== 'string' && !Number.isSafeInteger(valor)) return '';
  const texto = String(valor).trim();
  return /^[0-9]{1,20}$/.test(texto) ? texto.replace(/^0+/, '') : '';
}

function texto_(valor, limite) {
  if (typeof valor !== 'string') return null;
  const texto = valor.trim();
  return texto && texto.length <= limite && !/[\x00-\x1f\x7f]/.test(texto) ? texto : null;
}

function serialData_(valor) {
  if (typeof valor !== 'string' || !/^\d{4}-\d{2}-\d{2}$/.test(valor) || valor < '1900-01-01') return null;
  const data = new Date(valor + 'T00:00:00Z');
  if (isNaN(data.getTime()) || data.toISOString().slice(0, 10) !== valor) return null;
  // O serial representa a data de calendário, sem deslocamento de fuso horário.
  return (data.getTime() - Date.UTC(1899, 11, 30)) / 86400000;
}

function celulaTexto_(texto) {
  return { userEnteredValue: { stringValue: texto } };
}
