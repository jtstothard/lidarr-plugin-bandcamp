# Publication topology regression check. Ruby/Psych is bundled on GitHub Ubuntu runners.
require 'yaml'

def require_gate(condition, message)
  raise message unless condition
end

def validate(workflow)
  jobs = workflow.fetch('jobs')
  publish = jobs.fetch('publish')
  require_gate(publish.fetch('needs').sort == %w[build known-bad-compatibility-control].sort, 'Publication must require build and control success')
  require_gate(publish.fetch('if') == "startsWith(github.ref, 'refs/tags/v')", 'Publication must retain default success condition and tag restriction')
  require_gate(jobs.values.none? { |j| j['continue-on-error'] }, 'Jobs cannot waive failures')
  jobs.each do |name, job|
    job.fetch('steps').each do |step|
      if step.fetch('uses', '').include?('action-gh-release')
        require_gate(name == 'publish', 'Publication outside gated job')
      end
    end
  end
  steps = jobs.fetch('build').fetch('steps')
  indices = ['Run tests', 'Zip plugin', 'Check compatibility gate and scanner controls', 'Upload checked distribution'].map do |name|
    steps.index { |s| s['name'] == name } or raise "Missing required step: #{name}"
  end
  require_gate(indices == indices.sort, 'Unit checks, package, gate and upload order changed')
  indices.each do |i|
    require_gate(!steps[i]['if'] && !steps[i]['continue-on-error'], 'Required step can be bypassed')
  end
  gate = steps[indices[2]].fetch('run')
  %w[test_scanner.py test_gate.py gate.py].each { |command| require_gate(gate.lines.any? { |line| line.strip.start_with?("python3 tests/PackagedCompatibility/#{command} ") || line.strip == "python3 tests/PackagedCompatibility/#{command}" }, "Missing #{command}") }
  require_gate(gate.include?('"${PLUGIN_NAME}.net8.0.zip" --receipt compatibility.json'), 'Gate must check uploaded ZIP')
  require_gate(!gate.match?(/\|\||;|&&/), 'Gate commands cannot waive failures or chain commands')
  upload = steps[indices[3]].fetch('with')
  require_gate(upload.fetch('name') == 'checked-distribution' && upload.fetch('path').include?('${{ env.PLUGIN_NAME }}.net8.0.zip'), 'Upload must transport checked ZIP')
  require_gate(upload.fetch('path').include?('compatibility.json'), 'Receipt must travel with ZIP')
  control = jobs.fetch('known-bad-compatibility-control')
  require_gate(!control['if'], 'Known-bad control is required on all events')
  require_gate(control.fetch('steps').any? { |s| s['run'] == 'python3 tests/PackagedCompatibility/run.py' && !s['continue-on-error'] && !s['if'] }, 'Known-bad diagnostic must run')
  publication = publish.fetch('steps')
  download = publication.index { |s| s.fetch('uses', '').include?('download-artifact') && s.fetch('with')['name'] == 'checked-distribution' }
  verify = publication.index { |s| s.fetch('run', '').include?('verify_distribution.py "${PLUGIN_NAME}.net8.0.zip" compatibility.json') && !s['continue-on-error'] && !s['if'] }
  if verify
    command = publication[verify].fetch('run').strip
    require_gate(command == 'python3 tests/PackagedCompatibility/verify_distribution.py "${PLUGIN_NAME}.net8.0.zip" compatibility.json', 'Publication verification must be an unmodified command')
  end
  release = publication.index { |s| s.fetch('uses', '').include?('action-gh-release') }
  require_gate(download && verify && release && download < verify && verify < release, 'Download and byte verification must precede release')
  require_gate(publication[release].fetch('with').fetch('files').strip == '${{ env.PLUGIN_NAME }}.net8.0.zip', 'Release must publish checked ZIP')
end

workflow = YAML.load_file(ARGV[0] || '.github/workflows/build.yml')
validate(workflow)
# Prove the verifier catches lost dependencies and failure waivers.
[
  ->(w) { w['jobs']['build']['steps'].find { |s| s['name'] == 'Check compatibility gate and scanner controls' }['run'].gsub!('python3 tests/PackagedCompatibility/gate.py', 'python3 tests/PackagedCompatibility/run_matrix.py') },
  ->(w) { w['jobs']['build']['steps'].find { |s| s['name'] == 'Check compatibility gate and scanner controls' }['run'] += "\ntrue || echo ignored" },
  ->(w) { w['jobs']['publish']['steps'].find { |s| s.fetch('run', '').include?('verify_distribution.py') }['run'] += ' || true; true #' },
  ->(w) { w['jobs']['publish']['needs'].delete('build') },
  ->(w) { w['jobs']['publish']['needs'].delete('known-bad-compatibility-control') },
  ->(w) { w['jobs']['build']['continue-on-error'] = true },
  ->(w) { w['jobs']['build']['steps'].find { |s| s['name'] == 'Check compatibility gate and scanner controls' }['continue-on-error'] = true }
].each do |mutate|
  bad = Marshal.load(Marshal.dump(workflow))
  mutate.call(bad)
  rejected = false
  begin
    validate(bad)
  rescue RuntimeError
    rejected = true
  end
  require_gate(rejected, 'Broken topology control was accepted')
end
puts 'Publication requires successful unit/build/package/matrix/scan/control checks and verified transported ZIP; seven broken-topology controls rejected.'
