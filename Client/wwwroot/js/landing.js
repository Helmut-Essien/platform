// HelmutCode landing page enhancements. Loaded as a module (deferred, never render-blocking).
// Everything here is decorative: the page is complete and readable without it.
const reduce = matchMedia('(prefers-reduced-motion: reduce)').matches;
const coarse = matchMedia('(pointer: coarse)').matches;
document.documentElement.classList.add('js');

// Scroll reveal (opacity/transform only, so no layout shift).
const io = 'IntersectionObserver' in window && !reduce ? new IntersectionObserver((es) => {
  for (const e of es) if (e.isIntersecting) { e.target.classList.add('in'); io.unobserve(e.target); }
}, { rootMargin: '0px 0px -8% 0px' }) : null;
document.querySelectorAll('.reveal').forEach((el) => io ? io.observe(el) : el.classList.add('in'));

// Perspective tilt on cards (fine pointers only).
if (!reduce && !coarse) {
  document.querySelectorAll('.tilt').forEach((el) => {
    let raf = 0;
    el.addEventListener('pointermove', (ev) => {
      const r = el.getBoundingClientRect();
      const x = (ev.clientX - r.left) / r.width - 0.5, y = (ev.clientY - r.top) / r.height - 0.5;
      cancelAnimationFrame(raf);
      raf = requestAnimationFrame(() => {
        el.style.setProperty('--ry', (x * 10).toFixed(2) + 'deg');
        el.style.setProperty('--rx', (-y * 10).toFixed(2) + 'deg');
        el.style.setProperty('--mx', ((x + 0.5) * 100).toFixed(1) + '%');
        el.style.setProperty('--my', ((y + 0.5) * 100).toFixed(1) + '%');
      });
    });
    el.addEventListener('pointerleave', () => { el.style.removeProperty('--rx'); el.style.removeProperty('--ry'); });
  });

  // Hero invoice stack follows the pointer for layered depth.
  const stack = document.querySelector('.stack');
  const hero = document.querySelector('.hero');
  if (stack && hero) {
    let raf = 0;
    hero.addEventListener('pointermove', (ev) => {
      const x = ev.clientX / innerWidth - 0.5, y = ev.clientY / innerHeight - 0.5;
      cancelAnimationFrame(raf);
      raf = requestAnimationFrame(() => {
        stack.style.setProperty('--py', (x * 16).toFixed(2) + 'deg');
        stack.style.setProperty('--px', (-y * 12).toFixed(2) + 'deg');
      });
    });
  }
}

// WebGL hero scene: a slowly rotating globe of points with orbit rings.
// Raw WebGL (~3 KB) instead of a 600 KB library; started after load + idle.
function startScene() {
  const canvas = document.getElementById('scene');
  if (!canvas) return;
  const gl = canvas.getContext('webgl', { antialias: false, alpha: true, powerPreference: 'low-power', premultipliedAlpha: false });
  if (!gl) return; // CSS orb fallback stays visible.

  const vs = `attribute vec3 p;attribute float k;uniform mat4 m;uniform float s;uniform float t;varying float a;varying float kk;
  void main(){vec4 q=m*vec4(p,1.);q.z-=3.2;gl_Position=vec4(q.x*1.6/s,q.y*1.6,q.z*.1,-q.z);
  float d=clamp((q.z+4.4)/2.4,0.,1.);a=.45+.55*d;kk=k;gl_PointSize=(k>.5?3.6:4.4)*(1.+d)*${Math.min(devicePixelRatio || 1, 2).toFixed(1)}*(1.+.25*sin(t*2.+p.x*9.));}`;
  const fs = `precision mediump float;varying float a;varying float kk;uniform vec3 c1;uniform vec3 c2;
  void main(){vec2 u=gl_PointCoord-.5;float r=dot(u,u);if(r>.25)discard;float g=smoothstep(.25,0.,r);
  gl_FragColor=vec4(mix(c1,c2,kk),g*a);}`;
  const sh = (type, src) => { const s = gl.createShader(type); gl.shaderSource(s, src); gl.compileShader(s); return s; };
  const prog = gl.createProgram();
  gl.attachShader(prog, sh(gl.VERTEX_SHADER, vs)); gl.attachShader(prog, sh(gl.FRAGMENT_SHADER, fs));
  gl.linkProgram(prog);
  if (!gl.getProgramParameter(prog, gl.LINK_STATUS)) return;
  gl.useProgram(prog);

  const small = innerWidth < 700;
  const N = small ? 900 : 1600, R = small ? 260 : 420;
  const pts = [];
  const golden = Math.PI * (3 - Math.sqrt(5));
  for (let i = 0; i < N; i++) { const y = 1 - (i / (N - 1)) * 2, r = Math.sqrt(1 - y * y), th = golden * i; pts.push(Math.cos(th) * r, y, Math.sin(th) * r, 0); }
  const rings = [[1.45, 0.35], [1.75, -0.5], [1.25, 1.1]];
  for (const [rad, tilt] of rings) for (let i = 0; i < R / 3; i++) {
    const a = (i / (R / 3)) * Math.PI * 2, x = Math.cos(a) * rad, z = Math.sin(a) * rad;
    pts.push(x, z * Math.sin(tilt), z * Math.cos(tilt), 1);
  }
  const data = new Float32Array(pts), count = data.length / 4;
  const buf = gl.createBuffer(); gl.bindBuffer(gl.ARRAY_BUFFER, buf); gl.bufferData(gl.ARRAY_BUFFER, data, gl.STATIC_DRAW);
  const lp = gl.getAttribLocation(prog, 'p'), lk = gl.getAttribLocation(prog, 'k');
  gl.enableVertexAttribArray(lp); gl.vertexAttribPointer(lp, 3, gl.FLOAT, false, 16, 0);
  gl.enableVertexAttribArray(lk); gl.vertexAttribPointer(lk, 1, gl.FLOAT, false, 16, 12);
  const um = gl.getUniformLocation(prog, 'm'), us = gl.getUniformLocation(prog, 's'), ut = gl.getUniformLocation(prog, 't');
  const light = matchMedia('(prefers-color-scheme: light)');
  const setColors = () => {
    const l = light.matches;
    gl.uniform3fv(gl.getUniformLocation(prog, 'c1'), l ? [0.23, 0.44, 0.08] : [0.57, 0.85, 0.35]);
    gl.uniform3fv(gl.getUniformLocation(prog, 'c2'), l ? [0.85, 0.55, 0.05] : [0.98, 0.78, 0.3]);
  };
  setColors(); light.addEventListener?.('change', setColors);
  gl.enable(gl.BLEND); gl.blendFunc(gl.SRC_ALPHA, gl.ONE);

  const dpr = Math.min(devicePixelRatio || 1, small ? 1.5 : 2);
  const resize = () => {
    const w = canvas.clientWidth, h = canvas.clientHeight;
    canvas.width = Math.max(1, Math.round(w * dpr)); canvas.height = Math.max(1, Math.round(h * dpr));
    gl.viewport(0, 0, canvas.width, canvas.height);
    gl.uniform1f(us, w / Math.max(1, h));
  };
  resize();
  new ResizeObserver(resize).observe(canvas);

  let visible = true, running = false, t0 = performance.now(), tx = 0, ty = 0, cx = 0, cy = 0;
  if (!coarse) addEventListener('pointermove', (e) => { tx = (e.clientY / innerHeight - 0.5) * 0.4; ty = (e.clientX / innerWidth - 0.5) * 0.6; }, { passive: true });
  const frame = (now) => {
    if (!visible || document.hidden) { running = false; return; }
    const t = reduce ? 0.6 : (now - t0) / 1000;
    cx += (tx - cx) * 0.05; cy += (ty - cy) * 0.05;
    const ay = t * 0.12 + cy, ax = 0.35 + cx;
    const c = Math.cos(ay), s = Math.sin(ay), c2 = Math.cos(ax), s2 = Math.sin(ax);
    // rotation Y then X (column-major)
    gl.uniformMatrix4fv(um, false, new Float32Array([c, s * s2, -s * c2, 0, 0, c2, s2, 0, s, -c * s2, c * c2, 0, 0, 0, 0, 1]));
    gl.uniform1f(ut, t);
    gl.clearColor(0, 0, 0, 0); gl.clear(gl.COLOR_BUFFER_BIT);
    gl.drawArrays(gl.POINTS, 0, count);
    if (reduce) { running = false; return; } // one static frame
    requestAnimationFrame(frame);
  };
  const kick = () => { if (!running && visible && !document.hidden) { running = true; requestAnimationFrame(frame); } };
  new IntersectionObserver(([e]) => { visible = e.isIntersecting; kick(); }).observe(canvas);
  document.addEventListener('visibilitychange', kick);
  canvas.classList.add('on');
  kick();
}

const idle = (fn) => ('requestIdleCallback' in window ? requestIdleCallback(fn, { timeout: 1500 }) : setTimeout(fn, 200));
if (document.readyState === 'complete') idle(startScene); else addEventListener('load', () => idle(startScene), { once: true });
