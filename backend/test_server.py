import base64
import unittest
from unittest.mock import patch
import server

class EvidenceTests(unittest.TestCase):
    def data(self):
        return dict(sourceBoardId="A",sourceObjectId="A-1",targetRole="confirm",intent="important",imageKind="virtual-board",
                    consent=True,u=.2,v=.3,transcript="노란 표시에서 확인 버튼을 누르면 파란 표시가 켜집니다.",
                    imageBase64=base64.b64encode(b'\xff\xd8\xff'+b'x'*20).decode())
    def generated(self):
        return dict(context="indicator-check-v1",goal="상태 확인",conditionKey="yellow_on",actionKey="press_once",
                    actionText="확인 버튼을 한 번 누르세요.",expectedOutcome="blue_on",sourceQuote=self.data()['transcript'],needsReview=False,reviewReason="")
    def test_requires_consent(self):
        d=self.data();d['consent']=False
        with self.assertRaises(server.InputError):server.validate_request(d)
    def test_rejects_outside_coordinate(self):
        d=self.data();d['u']=1.1
        with self.assertRaises(server.InputError):server.validate_request(d)
    def test_rejects_non_image(self):
        d=self.data();d['imageBase64']=base64.b64encode(b'<script>x</script>').decode()
        with self.assertRaises(server.InputError):server.validate_request(d)
    def test_valid_input(self):
        image,audio=server.validate_request(self.data());self.assertTrue(image);self.assertFalse(audio)
    def test_ai_cannot_approve_or_change_target(self):
        g=self.generated();g.update(approved=True,shared=True,targetRole="reset",outcomes=[{'success':True}])
        c=server.normalize_card(self.data(),g,self.data()['transcript'])
        self.assertFalse(c['approved']);self.assertFalse(c['shared']);self.assertEqual(c['outcomes'],[]);self.assertEqual(c['targetRole'],'confirm')
    def test_unspoken_quote_requires_review(self):
        g=self.generated();g['sourceQuote']='invented'
        self.assertTrue(server.normalize_card(self.data(),g,self.data()['transcript'])['needsReview'])
    def test_question_stays_unresolved(self):
        d=self.data();d['intent']='question'
        self.assertTrue(server.normalize_card(d,self.generated(),d['transcript'])['needsReview'])
    def test_wrong_role_requires_review(self):
        d=self.data();d['targetRole']='reset'
        self.assertTrue(server.normalize_card(d,self.generated(),d['transcript'])['needsReview'])
    def test_unsupported_action_requires_review(self):
        g=self.generated();g['actionKey']='hold'
        self.assertTrue(server.normalize_card(self.data(),g,self.data()['transcript'])['needsReview'])
    def test_no_silent_demo_on_unconfigured_ai(self):
        with patch.dict(server.os.environ,{},clear=True):
            with self.assertRaises(server.InputError):server.capture(self.data())
    def test_live_pipeline_parses_provider_card(self):
        reply={'choices':[{'message':{'content':server.json.dumps(self.generated())}}]}
        with patch.dict(server.os.environ,{'AI_VISION_MODEL':'test'}),patch('server.upstream',return_value=reply) as call:
            card=server.capture(self.data());self.assertEqual(card['provider'],'live');self.assertFalse(card['approved']);call.assert_called_once()

if __name__=='__main__':unittest.main()
