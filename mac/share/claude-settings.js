// Claude Code 상태 표시줄(~/.claude/settings.json 의 statusLine)에 AI 사용량 앱을 걸고 푼다.
// 설치.sh·지우기.sh 가 osascript -l JavaScript 로 부른다(맥 기본 기능만 써서 jq·node 가 없어도 된다).
//   install   <settings> <앱 실행 파일> <연결 스크립트> <원래 설정 보관 파일> <백업 파일>
//   uninstall <settings> <앱 실행 파일> <연결 스크립트> <원래 설정 보관 파일> <백업 파일>
// 앱도 「Claude Code 에 연결」 메뉴와, 앱을 옮겨 경로가 끊겼을 때 같은 install 을 부른다.
// 결과 한 단어를 돌려준다: new · wrapped · repaired · already · restored · removed · untouched · error:<이유>
ObjC.import('Foundation');

function read(path) {
  const s = $.NSString.stringWithContentsOfFileEncodingError(path, $.NSUTF8StringEncoding, null);
  return s.isNil() ? null : s.js;
}

// settings.json 이 다른 곳으로 이어진 링크일 수 있어 통째로 바꾸지 않고 그 자리에 쓴다
function write(path, text, atomic) {
  if (!$(text).writeToFileAtomicallyEncodingError(path, atomic, $.NSUTF8StringEncoding, null)) {
    throw new Error(path + ' 에 쓰지 못했습니다');
  }
}

const isOurs = c => typeof c === 'string' && (c.includes('AIUsagePet') || c.includes('ai-usage-statusline.sh'));

function wrapperScript(bin, original) {
  return [
    '#!/bin/bash',
    '# AI 사용량 앱 연결 — Claude 한도를 앱이 읽도록 남기고, 원래 상태 표시줄은 그대로 보여 준다.',
    '# 원래 설정은 ai-usage-statusline.original.json 에 있고, 설치 폴더의 지우기.sh 로 되돌린다.',
    'input=$(cat)',
    `printf '%s' "$input" | "${bin}" --statusline >/dev/null 2>&1`,
    `printf '%s' "$input" | {`,
    original,
    '}',
    '',
  ].join('\n');
}

function run(argv) {
  const [mode, settingsPath, bin, wrapperPath, savedPath, backupPath] = argv;
  const text = read(settingsPath);
  let root = {};
  if (text !== null && text.trim() !== '') {
    try { root = JSON.parse(text); } catch (e) { return 'error:settings.json 이 올바른 JSON 이 아닙니다'; }
    if (typeof root !== 'object' || root === null || Array.isArray(root)) return 'error:settings.json 이 JSON 객체가 아닙니다';
  }
  const current = root.statusLine;
  const command = current && typeof current === 'object' ? current.command : undefined;
  const save = () => {
    if (text !== null) write(backupPath, text, true);
    write(settingsPath, JSON.stringify(root, null, 2) + '\n', false);
  };

  if (mode === 'install') {
    if (isOurs(command)) {
      // 앱을 옮기거나 다시 깔아 경로가 바뀌었으면 새 경로로 고쳐 건다
      // 「사용량」 같은 한글 경로는 설치.sh(합친 꼴)와 앱이 아는 경로(풀어 쓴 꼴)가 달라 맞춰서 비교한다
      const same = (a, b) => a.normalize('NFC') === b.normalize('NFC');
      const want = `"${bin}" --statusline`;
      if (command.includes('ai-usage-statusline.sh')) {
        const script = read(wrapperPath);
        if (script !== null && script.normalize('NFC').includes(want.normalize('NFC'))) return 'already';
        const saved = read(savedPath);
        let original;
        try { original = saved === null ? undefined : JSON.parse(saved).command; } catch (e) { original = undefined; }
        if (typeof original !== 'string' || original.trim() === '') return 'error:원래 상태 표시줄 설정(' + savedPath + ')을 읽지 못했습니다';
        write(wrapperPath, wrapperScript(bin, original), true);
        return 'repaired';
      }
      if (same(command, want)) return 'already';
      root.statusLine = Object.assign({}, current, { type: 'command', command: want });
      save();
      return 'repaired';
    }
    if (typeof command === 'string' && command.trim() !== '') {
      // 쓰던 상태 표시줄이 있으면 그대로 보이게 감싸고, 원래 설정을 따로 남겨 둔다
      write(savedPath, JSON.stringify(current, null, 2) + '\n', true);
      write(wrapperPath, wrapperScript(bin, command), true);
      root.statusLine = Object.assign({}, current, { type: 'command', command: `"${wrapperPath}"` });
      save();
      return 'wrapped';
    }
    root.statusLine = { type: 'command', command: `"${bin}" --statusline` };
    save();
    return 'new';
  }

  if (mode === 'uninstall') {
    if (!isOurs(command)) return 'untouched';
    const saved = read(savedPath);
    if (saved !== null) {
      root.statusLine = JSON.parse(saved);
    } else {
      delete root.statusLine;
    }
    save();
    return saved !== null ? 'restored' : 'removed';
  }
  return 'error:알 수 없는 모드 ' + mode;
}
