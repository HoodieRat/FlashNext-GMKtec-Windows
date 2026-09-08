// Model-free template/parser regression check linked against the bundled runtime.
#include "chat.h"
#include <fstream>
#include <iostream>
#include <stdexcept>

int main(int argc, char ** argv) {
    try {
        if (argc != 2) throw std::runtime_error("Expected a chat template path");
        std::ifstream file(argv[1]);
        if (!file) throw std::runtime_error("Template not found");
        const std::string source((std::istreambuf_iterator<char>(file)), {});
        auto templates = common_chat_templates_init(nullptr, source);
        common_json checks = common_json::array();
        for (bool thinking : {false, true}) {
            common_chat_templates_inputs inputs;
            inputs.enable_thinking = thinking;
            inputs.reasoning_format = COMMON_REASONING_FORMAT_DEEPSEEK;
            inputs.chat_template_kwargs = {{"enable_thinking", thinking ? "true" : "false"}, {"preserve_thinking", "true"}};
            inputs.messages = common_chat_msgs_parse_oaicompat(common_json::array({
                {{"role", "system"}, {"content", "Prefix verification fixture."}},
                {{"role", "user"}, {"content", "First question."}}
            }));
            const auto initial = common_chat_templates_apply(templates.get(), inputs);
            const std::string answer = "Synthetic answer.\n";
            const std::string generated = (thinking ? "Check the fixture.\n</think>\n\n" : "") + answer;
            common_chat_parser_params parser(initial);
            parser.reasoning_format = COMMON_REASONING_FORMAT_DEEPSEEK;
            if (!initial.parser.empty()) parser.parser.load(initial.parser);
            auto assistant = common_chat_parse(generated, false, parser);
            assistant.role = "assistant";
            inputs.messages.push_back(assistant);
            common_chat_msg user;
            user.role = "user";
            user.content = "Follow up.";
            inputs.messages.push_back(user);
            const auto following = common_chat_templates_apply(templates.get(), inputs);
            const std::string completed = initial.prompt + generated;
            size_t common = 0;
            while (common < completed.size() && common < following.prompt.size() && completed[common] == following.prompt[common]) ++common;
            if (common != completed.size()) {
                std::cerr << "Replay diverged at byte " << common << " of " << completed.size() << "\n"
                          << "Expected: " << common_json(completed).dump() << "\nActual: " << common_json(following.prompt).dump() << "\n";
                return 1;
            }
            checks.push_back({{"thinking", thinking}, {"completed", completed}, {"following", following.prompt}, {"preservedPrefixBytes", common}});
        }
        std::cout << checks.dump(2) << std::endl;
        return 0;
    } catch (const std::exception & error) {
        std::cerr << error.what() << std::endl;
        return 1;
    }
}
